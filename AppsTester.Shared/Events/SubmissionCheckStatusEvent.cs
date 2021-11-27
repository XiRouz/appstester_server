using System;
using EasyNetQ;
using Newtonsoft.Json;

namespace AppsTester.Shared.Events
{
    [Queue(queueName: "Submissions.ChecksStatusEvents")]
    public class SubmissionCheckStatusEvent : SubmissionCheckEvent
    {
        public string SerializedStatus { get; set; }

        public SubmissionCheckStatusEvent WithStatus(object status)
        {
            SerializedStatus = JsonConvert.SerializeObject(status);

            return this;
        }

        public TStatus GetStatus<TStatus>()
        {
            return JsonConvert.DeserializeObject<TStatus>(SerializedStatus);
        }
    }
}