// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DepotDumper
{
    class DumpConfig
    {
        public int CellID { get; set; }
        public string DumpDirectory { get; set; }

        public int MaxServers { get; set; }
        public int MaxDownloads { get; set; }

        public bool RememberPassword { get; set; }

        // A Steam LoginID to allow multiple concurrent connections
        public uint? LoginID { get; set; }

        public bool UseQrCode { get; set; }
    }
}
