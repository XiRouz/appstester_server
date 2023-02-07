using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Files;
using AppsTester.Controller.Services;
using AppsTester.Controller.Services.Moodle;
using AppsTester.Shared.RabbitMq;
using AppsTester.Shared.SubmissionChecker.Events;
using EasyNetQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AppsTester.Controller.Submissions
{
    internal class SubmissionsInfoSynchronizer : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRabbitBusProvider _rabbitBusProvider;
        private readonly IMoodleService _moodleService;
        private readonly ILogger<SubmissionsInfoSynchronizer> _logger;


        public SubmissionsInfoSynchronizer(
            IServiceScopeFactory serviceScopeFactory,
            IRabbitBusProvider rabbitBusProvider,
            ILogger<SubmissionsInfoSynchronizer> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _rabbitBusProvider = rabbitBusProvider;
            using var scope = serviceScopeFactory.CreateScope();
            _moodleService = scope.ServiceProvider.GetRequiredService<IMoodleService>();
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (true)
            {
                try
                {
                    var submissionsUnits = await _moodleService.GetSubmissionsToCheckAsync(stoppingToken);
                    if (submissionsUnits == null || !submissionsUnits.Any())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }

                    using var serviceScope = _serviceScopeFactory.CreateScope();
                    await using var dbContext = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    foreach (var submissionUnit in submissionsUnits)
                    {
                        var attemptId = submissionUnit.AttemptId;
                        var attemptStepIds = submissionUnit.AttemptStepsIds;
                        var lastSendingDateTime = await dbContext.SubmissionChecks
                            .Where(x => x.AttemptId == attemptId)
                            .Select(x => x.SendingDateTimeUtc)
                            .OrderByDescending(x => x)
                            .FirstOrDefaultAsync(stoppingToken);
                        foreach (var attemptStepId in attemptStepIds)
                        {
                            try
                            {
                                var sc = await dbContext.SubmissionChecks
                                    .Where(x => x.AttemptStepId == attemptStepId)
                                    .OrderByDescending(s => s.SendingDateTimeUtc).FirstOrDefaultAsync(stoppingToken);

                                if (sc is not null && sc.SendingDateTimeUtc != lastSendingDateTime)
                                {
                                    await _moodleService.SetSubmissionStatusAsync(sc.AttemptStepId, sc.LastSerializedStatus, stoppingToken);
                                    await _moodleService.SetSubmissionResultAsync(sc.AttemptStepId, sc.SerializedResult,
                                        stoppingToken);
                                }
                                else if (sc is null || sc.SendingDateTimeUtc == lastSendingDateTime &&
                                         sc.SerializedResult is not null)
                                {
                                    var submission = await _moodleService.GetSubmissionAsync(attemptStepId, stoppingToken);

                                    var fileCache = serviceScope.ServiceProvider.GetRequiredService<FileCache>();

                                    var missingFiles = submission
                                        .Files
                                        .Where(pair => pair.Key.EndsWith("_hash"))
                                        .Where(pair => !fileCache.IsKeyExists(pair.Value))
                                        .ToList();

                                    if (missingFiles.Any())
                                    {
                                        submission = await _moodleService.GetSubmissionAsync(attemptStepId, stoppingToken,
                                            string.Join(",",
                                                missingFiles.Select(mf => mf.Value)));

                                        foreach (var (fileName, fileHash) in missingFiles)
                                            fileCache.Write(fileHash,
                                                Convert.FromBase64String(
                                                    submission.Files[fileName.Substring(0, fileName.Length - 5)]));
                                    }

                                    var submissionId = Guid.NewGuid();
                                    var submissionCheckRequest = new SubmissionCheckRequestEvent
                                    {
                                        SubmissionId = submissionId,
                                        Files = submission.Files.Where(f => f.Key.EndsWith("_hash"))
                                            .ToDictionary(pair => pair.Key.Substring(0, pair.Key.Length - 5),
                                                pair => pair.Value),
                                        PlainParameters = submission.Parameters
                                    };
                                    var submissionCheck = new SubmissionCheck
                                    {
                                        Id = submissionId,
                                        AttemptId = attemptId,
                                        AttemptStepId = attemptStepId,
                                        SendingDateTimeUtc = DateTime.UtcNow,
                                        SerializedRequest = JsonConvert.SerializeObject(submissionCheckRequest)
                                    };
                                    dbContext.SubmissionChecks.Add(submissionCheck);

                                    var rabbitConnection = _rabbitBusProvider.GetRabbitBus();
                                    await rabbitConnection.PubSub.PublishAsync(submissionCheckRequest,
                                        topic: submission.CheckerSystemName,
                                        cancellationToken: stoppingToken);

                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "can't handle attempt {attemptId}", attemptId);
                            }
                            finally
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                            }
                        }                        
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "unhandled exception");
                }
                finally
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
    }
}