using System.Net;
using System.Net.Sockets;
using static System.Console;

namespace StunTrackersFiltering
{
    public static class Program
    {
        static Random random = new Random(DateTime.Now.Millisecond);
        static IPAddress externalIpAddress;
        static UdpClient udpClient = new UdpClient()
        {
            DontFragment = true,
            Client =
                    {
                        SendTimeout = 5000,
                        ReceiveTimeout = 5000
                    }
        };
        static HttpClient httpClient = new HttpClient();

        static int trackersCount = 0;
        static int trackersWorking = 0;
        static int stunsCount = 0;
        static int stunsWorking = 0;

        private static string[] SplitLines(string input)
        {
            return input.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static IList<string> PreprocessHostList(IEnumerable<string> input)
        {
            var h = new HashSet<string>();
            foreach (var item in input)
                h.Add(item.Trim().ToLowerInvariant().Replace("udp://", "").Replace("/announce", ""));
            var result = h.ToList();
            result.Sort();
            return result;
        }

        private static async Task<string[]> DownloadList(string url)
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(10000);
            var content = await httpClient.GetStringAsync(url, cts.Token);
            return SplitLines(content);
        }

        private static bool SequenceEqual(byte[] a, int aOffset, byte[] b, int bOffset, int length)
        {
            for (var index = 0; index < length; index++)
                if (a[aOffset + index] != b[bOffset + index])
                    return false;

            return true;
        }

        private static async Task GetExternalIpAddress()
        {
            string externalIpString = (await httpClient.GetStringAsync("http://icanhazip.com")).Replace("\\r\\n", "").Replace("\\n", "").Trim();
            externalIpAddress = IPAddress.Parse(externalIpString);
            WriteLine($"External IP address is: {externalIpAddress}");
            WriteLine("----------");
            WriteLine();
        }

        public static async Task Main(string[] args)
        {
            await GetExternalIpAddress();
            await TestTrackers();
            await TestStunServers();

            WriteLine("Statistics:");
            WriteLine($" - {trackersWorking}/{trackersCount} trackers");
            WriteLine($" - {stunsWorking}/{stunsCount} stuns");
        }

        #region Trackers

        private static async Task TestTrackers()
        {
            var trackersContent = File.ReadAllText("trackers-links.txt");
            var trackerLinks = SplitLines(trackersContent);
            var trackersRaw = new List<string>();

            foreach (var trackerLink in trackerLinks)
                trackersRaw.AddRange(await DownloadList(trackerLink));

            var trackers = PreprocessHostList(trackersRaw);
            var trackersAddressLength = trackers.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length + 2;
            trackersCount = trackers.Count;

            using (var trackerWriter = new StreamWriter("trackers.txt"))
                for (var i = 0; i < trackers.Count; i++)
                {
                    try
                    {
                        var result = await TestTracker(trackers[i]);
                        if (!result)
                        {
                            WriteLine($"{trackers[i].PadLeft(trackersAddressLength)}: Failed (either not giving correct address or endianness)");
                            continue;
                        }
                        trackerWriter.WriteLine(trackers[i]);
                        WriteLine($"{trackers[i].PadLeft(trackersAddressLength)}: OK");
                        trackersWorking += 1;
                    }
                    catch (Exception ex)
                    {
                        Error.WriteLine($"{trackers[i].PadLeft(trackersAddressLength)}: {ex.Message}");
                    }
                }
            WriteLine("----------");
            WriteLine();
        }

        private static async Task<bool> TestTracker(string host)
        {
            var port = (ushort)random.Next(1024, 65500);
            var result = await Announce(host, port);

            bool correctEndianness = result.Any((ep) => ep.Port == port);
            bool ipMatchAll = result.All(ep => ep.Address.Equals(externalIpAddress));
            bool ipMatchAny = result.Any(ep => ep.Address.Equals(externalIpAddress));
            if (!ipMatchAll && ipMatchAny)
                WriteLine($"     ---> inject ip: " + string.Join(", ", result));

            return correctEndianness && ipMatchAny;
        }

        public static async Task<List<IPEndPoint>> Announce(string tracker, ushort listenPort)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(5000);

            var cid = new byte[] { 0, 0, 4, 23, 39, 16, 25, 128 };
            var txnId = new byte[4];
            random.NextBytes(txnId);

            var parts = tracker.Split(':');
            var hostname = parts[0];
            var port = ushort.Parse(parts[1]);

            await udpClient.SendAsync(new byte[] { 0, 0, 4, 23, 39, 16, 25, 128, 0, 0, 0, 0, txnId[0], txnId[1], txnId[2], txnId[3] }, 16, hostname, port);

            byte[] recBuf;

            try
            {                
                recBuf = (await udpClient.ReceiveAsync(cts.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Connect: Timed out");
            }

            if (recBuf.Length == 0)
                throw new InvalidDataException("Connect: Empty data received.");

            if (recBuf.Length < 16)
                throw new InvalidDataException($"Connect: Invalid data received ({recBuf.Length} bytes, supposed to be more than 16 bytes)");

            if (recBuf[0] != 0 || recBuf[1] != 0 || recBuf[2] != 0 || recBuf[3] != 0)
                throw new InvalidDataException($"Connect: Invalid data received (action: {BitConverter.ToString(recBuf, 0, 4)}, supposed to be 00-00-00-00)");

            if (!SequenceEqual(recBuf, 4, txnId, 0, 4))
                throw new InvalidDataException($"Connect: Invalid data received (txn id: {BitConverter.ToString(recBuf, 4, 4)}, supposed to be {BitConverter.ToString(txnId, 0, 4)})");

            Buffer.BlockCopy(recBuf, 8, cid, 0, 8);

            random.NextBytes(txnId);
            var hash = new byte[20];
            random.NextBytes(hash);

            var sendBuf = new byte[]
            {
                cid[0], cid[1], cid[2], cid[3], cid[4], cid[5], cid[6], cid[7], 0, 0, 0, 1, txnId[0], txnId[1], txnId[2], txnId[3], hash[0], hash[1], hash[2], hash[3], hash[4],
                hash[5], hash[6], hash[7], hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15], hash[16], hash[17], hash[18], hash[19], 71, 105, 116,
                72, 117, 98, 65, 99, 116, 105, 111, 110, 115, cid[0], cid[1], cid[2], cid[3], cid[4], cid[5], cid[6], 0, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 16, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, txnId[3], txnId[1], txnId[2], txnId[0], 255, 255, 255, 255, (byte)(listenPort >> 8), (byte)listenPort, 0, 0
            };

            await udpClient.SendAsync(sendBuf, sendBuf.Length, hostname, port);

            try
            {
                recBuf = (await udpClient.ReceiveAsync(cts.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Announce: Timed out.");
            }

            if (recBuf.Length < 20)
                throw new InvalidDataException($"Announce: Invalid data received ({recBuf.Length} bytes, supposed to be more than 16 bytes)");

            if (recBuf[0] != 0 || recBuf[1] != 0 || recBuf[2] != 0 || recBuf[3] != 1)
                throw new InvalidDataException($"Announce: Invalid data received (action: {BitConverter.ToString(recBuf, 0, 4)}, supposed to be 00-00-00-01)");

            if (!SequenceEqual(recBuf, 4, txnId, 0, 4))
                throw new InvalidDataException($"Announce: Invalid data received (txn id: {BitConverter.ToString(recBuf, 4, 4)}, supposed to be {BitConverter.ToString(txnId, 0, 4)})");

            var returnValue = new List<IPEndPoint>();
            for (var i = 20; i < recBuf.Length; i += 6)
            {
                var peerPort = unchecked((ushort)(recBuf[i + 5] + (recBuf[i + 4] << 8)));
                returnValue.Add(new IPEndPoint(new IPAddress(new byte[] { recBuf[i], recBuf[i + 1], recBuf[i + 2], recBuf[i + 3] }), peerPort));
            }

            return returnValue;
        }

        #endregion

        #region Stun 

        static byte[] bindingRequestHeader = new byte[] { 0, 1, 0, 0, 33, 18, 164, 66, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        private static async Task TestStunServers()
        {
            var stunServersContent = File.ReadAllText("stun-servers-links.txt");
            var stunServersLinks = SplitLines(stunServersContent);
            var stunServersRaw = new List<string>();

            foreach (var stunServersLink in stunServersLinks)
                stunServersRaw.AddRange(await DownloadList(stunServersLink));

            var stunServers = PreprocessHostList(stunServersRaw);
            var stunServersAddressLength = stunServers.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length + 2;
            stunsCount = stunServers.Count;

            using (var stunServerWriter = new StreamWriter("stun-servers.txt"))
                for (var i = 0; i < stunServers.Count; i++)
                {
                    try
                    {
                        var result = await TestStunServer(stunServers[i]);
                        stunServerWriter.WriteLine(stunServers[i]);
                        if (result.Address.Equals(externalIpAddress))                        
                        {
                            WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)}: OK");
                            stunsWorking += 1;
                        }
                        else
                            WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)}: Different external endpoint: got {result} vs {udpClient.Client.RemoteEndPoint as IPEndPoint} (external address: {externalIpAddress})");
                    }
                    catch (Exception ex)
                    {
                        Error.WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)}: {ex.Message}");
                    }
                }
            WriteLine("----------");
            WriteLine();
        }

        private static async Task<IPEndPoint> TestStunServer(string stunServer)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(2500);

            var parts = stunServer.Split(':');
            var stunHost = parts[0];
            var stunPort = ushort.Parse(parts[1]);

            for (var i = 8; i < 20; i++)
                bindingRequestHeader[i] = (byte)random.Next(0, 255);

            await udpClient.SendAsync(bindingRequestHeader, bindingRequestHeader.Length, stunHost, stunPort);

            byte[] b;
            try
            {
                b = (await udpClient.ReceiveAsync(cts.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Stun: Timed out.");
            }

            var index = 20;
            unchecked
            {
                IPEndPoint mappedEndpoint = null;

                while (index < b.Length)
                {
                    var type = unchecked((ushort)(b[index + 1] + (b[index] << 8)));
                    var attributeLength = unchecked((ushort)(b[index + 3] + (b[index + 2] << 8)));

                    if (type == 32)
                    {
                        var zero = b[index + 4];
                        if (zero != 0)
                            throw new InvalidDataException($"Stun: Invalid response 1, expected 00, got {b[index]}");

                        var family = b[index + 5];
                        int addressLength;
                        switch (family)
                        {
                            case 1: addressLength = 4; break;
                            case 2: addressLength = 16; break;
                            default: throw new InvalidDataException($"Stun: Invalid family 1, got {family}");
                        }

                        var port = unchecked((ushort)((byte)(b[index + 7] ^ b[5]) + ((byte)(b[index + 6] ^ b[4]) << 8)));

                        var addressBytes = new byte[addressLength];
                        for (var i = 0; i < addressLength; i++)
                            addressBytes[i] = (byte)(b[index + 8 + i] ^ b[4 + i]);

                        var ipAddress = new IPAddress(addressBytes);
                        return new IPEndPoint(ipAddress, port);
                    }
                    else if (type == 1)
                    {
                        var zero = b[index + 4];
                        if (zero != 0)
                            throw new InvalidDataException($"Stun: Invalid response 2, expected 00, got {b[index]}");

                        var family = b[index + 5];
                        int addressLength;
                        switch (family)
                        {
                            case 1: addressLength = 4; break;
                            case 2: addressLength = 16; break;
                            default: throw new InvalidDataException($"Stun: Invalid family 2, got {family}");
                        }

                        var port = unchecked((ushort)((byte)(b[index + 7]) + ((byte)(b[index + 6]) << 8)));
                        var addressBytes = new byte[addressLength];
                        for (var i = 0; i < addressLength; i++)
                            addressBytes[i] = (byte)(b[index + 8 + i]);

                        var ipAddress = new IPAddress(addressBytes);
                        mappedEndpoint = new IPEndPoint(ipAddress, port);

                        switch (family)
                        {
                            case 1: index += 8; break;
                            case 2: index += 20; break;
                        }
                    }
                    else
                    {
                        index += 4 + attributeLength;
                    }
                }

                if (mappedEndpoint != null)
                    return mappedEndpoint;

                throw new InvalidDataException("Stun: missing xor-mapped-address");
            }
        }

        #endregion
    }
}
