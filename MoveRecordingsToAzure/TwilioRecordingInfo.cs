using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoveRecordingsToAzure
{
    public class TwilioRecordingInfo
    {
        public string SID { get; set; }

        public string CallSID { get; set; }

        public string DateCreated { get; set; }

        public int Duration { get; set; }
    }
}
