using DnsClient;
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


        static LookupClient resolverCloudflare = new(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1"));
        static LookupClient resolverGoogle = new(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"));

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

        private static string[] DownloadList(string url)
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(10000);
            var content = httpClient.GetStringAsync(url, cts.Token).GetAwaiter().GetResult();
            return SplitLines(content);
        }

        private static bool SequenceEqual(byte[] a, int aOffset, byte[] b, int bOffset, int length)
        {
            for (var index = 0; index < length; index++)
                if (a[aOffset + index] != b[bOffset + index])
                    return false;

            return true;
        }

        private static void GetExternalIpAddress()
        {
            string externalIpString = httpClient.GetStringAsync("http://icanhazip.com").GetAwaiter().GetResult().Replace("\\r\\n", "").Replace("\\n", "").Trim();
            externalIpAddress = IPAddress.Parse(externalIpString);
            WriteLine($"External IP address is: {externalIpAddress}");
            WriteLine("----------");
            WriteLine();
        }

        private static IPAddress[] Resolve(string hostname)
        {
            HashSet<IPAddress> result = new HashSet<IPAddress>();

            try
            {
                var resultsCloudFlare = resolverCloudflare.Query(hostname, QueryType.A);
                foreach (var resultCloudFlare in resultsCloudFlare.Answers.ARecords())
                    result.Add(resultCloudFlare.Address);
            }
            catch { /* skip */ }

            try
            {
                var resultsGoogle = resolverGoogle.Query(hostname, QueryType.A);
                foreach (var resultGoogle in resultsGoogle.Answers.ARecords())
                    result.Add(resultGoogle.Address);
            }
            catch { /* skip */ }

            return result.ToArray();
        }

        public static void Main(string[] args)
        {
            GetExternalIpAddress();
            TestTrackers();
            TestStunServers();

            WriteLine("Statistics:");
            WriteLine($" - {trackersWorking}/{trackersCount} trackers");
            WriteLine($" - {stunsWorking}/{stunsCount} stuns");
        }

        #region Trackers

        private static void TestTrackers()
        {
            var trackersContent = File.ReadAllText("trackers-links.txt");
            var trackerLinks = SplitLines(trackersContent);
            var trackersRaw = new List<string>();

            foreach (var trackerLink in trackerLinks)
                trackersRaw.AddRange(DownloadList(trackerLink));

            var trackers = PreprocessHostList(trackersRaw);
            var trackersAddressLength = trackers.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length + 2;
            trackersCount = trackers.Count;

            using (var trackerWriter = new StreamWriter("trackers.txt"))
            using (var trackerIpWriter = new StreamWriter("trackers-ip.txt"))
                for (var i = 0; i < trackers.Count; i++)
                {
                    var hostOk = false;
                    var hostname = trackers[i];
                    var parts = hostname.Split(':');

                    var trackerHost = parts[0];
                    var trackerPort = ushort.Parse(parts[1]);
                    var addresses = Resolve(trackerHost);

                    trackerIpWriter.WriteLine(trackers[i] + ':');

                    foreach (var address in addresses)
                        try
                        {
                            var result = TestTracker(new IPEndPoint(address, trackerPort));
                            if (!result)
                            {
                                WriteLine($"{trackers[i].PadLeft(trackersAddressLength)} ({address}:{trackerPort}): Failed (either not giving correct address or endianness)");
                                continue;
                            }
                            hostOk = true;
                            trackerIpWriter.WriteLine($"{address}:{trackerPort}");
                            WriteLine($"{trackers[i].PadLeft(trackersAddressLength)} ({address}:{trackerPort}): OK");
                            trackersWorking += 1;
                        }
                        catch (Exception ex)
                        {
                            Error.WriteLine($"{trackers[i].PadLeft(trackersAddressLength)}: {ex.Message}");
                        }
                    if (hostOk)
                        trackerWriter.WriteLine(trackers[i]);
                }
            WriteLine("----------");
            WriteLine();
        }

        private static bool TestTracker(IPEndPoint ep)
        {
            var port = (ushort)random.Next(1024, 65500);
            var result = Announce(ep, port);

            bool correctEndianness = result.Any((ep) => ep.Port == port);
            bool ipMatchAll = result.All(ep => ep.Address.Equals(externalIpAddress));
            bool ipMatchAny = result.Any(ep => ep.Address.Equals(externalIpAddress));
            if (!ipMatchAll && ipMatchAny)
                WriteLine($"     ---> inject ip: " + string.Join(", ", result));

            return correctEndianness && ipMatchAny;
        }

        public static List<IPEndPoint> Announce(IPEndPoint tracker, ushort listenPort)
        {   
            var cid = new byte[] { 0, 0, 4, 23, 39, 16, 25, 128 };
            var txnId = new byte[4];
            random.NextBytes(txnId);

            udpClient.Send(new byte[] { 0, 0, 4, 23, 39, 16, 25, 128, 0, 0, 0, 0, txnId[0], txnId[1], txnId[2], txnId[3] }, 16, tracker);

            byte[] recBuf;
            IPEndPoint? anywhere = null;

            try
            {
                recBuf = udpClient.Receive(ref anywhere);
            }
            catch (SocketException)
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

            //Buffer.BlockCopy(recBuf, 8, cid, 0, 8);
            for (var idx=0; idx<8; idx++)
                cid[idx] = recBuf[idx+8];

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

            //await udpClient.SendAsync(sendBuf, sendBuf.Length, hostname, port).ConfigureAwait(false);
            udpClient.Send(sendBuf, sendBuf.Length, tracker);

            try
            {
                //recBuf = (await udpClient.ReceiveAsync(cts.Token).ConfigureAwait(false)).Buffer;
                recBuf = udpClient.Receive(ref anywhere);
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

        private static void TestStunServers()
        {
            var stunServersContent = File.ReadAllText("stun-servers-links.txt");
            var stunServersLinks = SplitLines(stunServersContent);
            var stunServersRaw = new List<string>();

            foreach (var stunServersLink in stunServersLinks)
                stunServersRaw.AddRange(DownloadList(stunServersLink));

            var stunServers = PreprocessHostList(stunServersRaw);
            var stunServersAddressLength = stunServers.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length + 2;
            stunsCount = stunServers.Count;

            using (var stunServerWriter = new StreamWriter("stun-servers.txt"))
            using (var stunServerIpWriter = new StreamWriter("stun-servers-ip.txt"))
                for (var i = 0; i < stunServers.Count; i++)
                {
                    var hostOk = false;
                    var hostname = stunServers[i];
                    var parts = hostname.Split(':');

                    var stunHost = parts[0];
                    var stunPort = ushort.Parse(parts[1]);
                    var addresses = Resolve(stunHost);

                    stunServerIpWriter.WriteLine(stunServers[i] + ':');

                    foreach (var address in addresses)
                    {
                        try
                        {
                            var result = TestStunServer(new IPEndPoint(address, stunPort));

                            if (result.Address.Equals(externalIpAddress))
                            {
                                hostOk = true;
                                WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)} ({address}:{stunPort}): OK");
                                stunServerIpWriter.WriteLine($"{address}:{stunPort}");
                                stunsWorking += 1;
                            }
                            else
                                WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)} ({address}:{stunPort}): Different external endpoint: got {result} vs {udpClient.Client.RemoteEndPoint as IPEndPoint} (external address: {externalIpAddress})");
                        }
                        catch (Exception ex)
                        {
                            Error.WriteLine($"{stunServers[i].PadLeft(stunServersAddressLength)}: {ex.Message}");
                        }
                    }
                    if (hostOk)
                        stunServerWriter.WriteLine(stunServers[i]);
                }

            WriteLine("----------");
            WriteLine();
        }

        private static IPEndPoint TestStunServer(IPEndPoint endpoint)
        {
            for (var i = 8; i < 20; i++)
                bindingRequestHeader[i] = (byte)random.Next(0, 255);

            udpClient.Send(bindingRequestHeader, bindingRequestHeader.Length, endpoint);

            byte[] b;
            try
            {
                IPEndPoint anywhere = null;
                b = udpClient.Receive(ref anywhere);
            }
            catch (SocketException)
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
