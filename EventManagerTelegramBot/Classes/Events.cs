using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EventManagerTelegramBot.Classes
{
    public class Events
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
        public Events(DateTime time, string message)
        {
            Time = time;
            Message = message;
        }
    }
}
