using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EventManagerTelegramBot.Classes
{
    public class Events
    {
        public int Id { get; set; }
        public long UserId { get; set; }
        public DateTime EventTime { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool IsRecurring { get; set; }
        public string? RecurringDays { get; set; }
        public DateTime? LastTriggered { get; set; }

        public Events() { }

        public Events(DateTime time, string message)
        {
            EventTime = time;
            Message = message;
        }
    }
}
