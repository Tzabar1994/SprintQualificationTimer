using System.IO.Enumeration;
using System.IO.Ports;
using System.Threading.Channels;

namespace SprintTimer
{
    static class SprintQualificationTimer
    {
        readonly static Dictionary<int, string> ChannelLookup = new()
        {
            {1, "start" }, {2, "split"}, {3, "finish"}
        };

        static List<RiderResult> _results = new();


        static async Task Main(string[] args)
        {
            var con = true;
            var channel = Channel.CreateUnbounded<TimeEvent>();
            var port = "";
            var comPorts = SerialPort.GetPortNames();
            
            if (comPorts.Length > 1) 
            {
                Console.WriteLine("The following ports are available. Please pick one!");
                foreach (var comPort in comPorts)
                {
                    Console.WriteLine(comPort.ToString());
                }

                port = Console.ReadLine();
            }
            else
            { 
                port = comPorts[0]; 
            }
            
            var st = new SprintTimer(channel, port ?? "COM3");


            Console.CancelKeyPress += delegate
            {
                Console.WriteLine("Exiting...");
                PrintResults();
                SaveResults();
                con = false;
                st.close();
                Environment.Exit(0);
            };

            var active = false;
            var currentBib = 0;

            while (con)
            {
                while (await channel.Reader.WaitToReadAsync())
                {
                    while (channel.Reader.TryRead(out var te))
                    {
                        if (!active)
                        {
                            if (te.Channel == 1)
                            {
                                Console.WriteLine("Pulse from {0} line at time {1}.\nAssign Bib or Ignore: ", ChannelLookup[te.Channel], te.Timestamp.ToString("HH:mm:ss.ffff"));
                                if (int.TryParse(Console.ReadLine(), out int bib))
                                {
                                    if (_results.Exists(x => x.Rider == bib))
                                    {
                                        Console.WriteLine($"Rider {bib} has already started. Overwrite ('w') or ignore ('i')?");
                                        if (Console.ReadLine() == "w")
                                        {
                                            var rider = _results.Find(x => x.Rider == bib);
                                            if (rider != null)
                                            {
                                                rider.StartTime = te.Timestamp;
                                                rider.SplitTime = null;
                                                rider.FinishTime = null;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _results.Add(new RiderResult(bib, te.Timestamp));
                                    }
                                    active = true;
                                    currentBib = bib;
                                }
                            }
                        }
                        else
                        {
                            var rider = _results.Find(x => x.Rider == currentBib);
                            if (rider != null)
                            {
                                if (te.Channel == 2 && rider.SplitTime == null)
                                {
                                    rider.SplitTime = te.Timestamp;
                                }
                                else if (te.Channel == 3 && rider.FinishTime == null)
                                {
                                    rider.FinishTime = te.Timestamp;
                                    active = false;
                                }
                            }
                        }
                        Console.Clear();
                        PrintResults();
                    }
                }
            }
        }

        public static void PrintResults()
        {
            Console.WriteLine("{6,4} \t {0,3} \t {1,15} \t {2,15} \t {3,15} \t {4,10} \t {5,10}",
                   "Bib", "Start", "100m", "200m", "Time", "Speed", "Rank");

            var rank = 1;
            foreach (var v in _results.OrderBy(x => x.GetDuration()))
            {
                Console.WriteLine("{6,4} \t {0,3} \t {1,15} \t {2,15} \t {3,15} \t {4,10} \t {5,10:F3}km/ph",
                    v.Rider,
                    v.StartTime.ToString("HH:mm:ss.ffff"),
                    v.SplitTime?.ToString("HH:mm:ss.ffff"),
                    v.FinishTime?.ToString("HH:mm:ss.ffff"),
                    v.GetDuration()?.ToString("s\\.ffff"),
                    v.GetSpeed(),
                    rank);

                rank++;
            }
        }

        public static void SaveResults()
        {
            string target = @"c:\results";
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
            }

            var filename = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + "-results.csv";

            Console.WriteLine(Path.Combine(target, filename));

            using (StreamWriter outputFile = new StreamWriter(Path.Combine(target, filename)))
            {
                outputFile.WriteLine("{0},{1},{2},{3},{4},{5}",
                   "Bib", "Start", "100m", "200m", "Time", "Speed");
                var rank = 1;
                foreach (var v in _results.OrderBy(x => x.GetDuration()))
                {
                    outputFile.WriteLine("{0},{1},{2},{3},{4},{5:F3}",
                        v.Rider,
                        v.StartTime.ToString("HH:mm:ss.ffff"),
                        v.SplitTime?.ToString("HH:mm:ss.ffff"),
                        v.FinishTime?.ToString("HH:mm:ss.ffff"),
                        v.GetDuration()?.ToString("s\\.ffff"),
                        v.GetSpeed());

                    rank++;
                }
            }
        }


    }

    public class RiderResult
    {
        public int Rider { get; set; }
        public TimeOnly StartTime { get; set; }
        public TimeOnly? SplitTime { get; set; }
        public TimeOnly? FinishTime { get; set; }

        public RiderResult(int rider, TimeOnly startTime)
        {
            this.Rider = rider;
            this.StartTime = startTime;
            SplitTime = null;
            FinishTime = null;
        }

        public TimeSpan? GetDuration()
        {
            if (this.FinishTime == null)
            {
                return null;
            }
            return this.FinishTime - this.StartTime;
        }

        public Decimal? GetSpeed()
        {
            var duration = this.GetDuration();

            if (this.FinishTime == null || duration == null)
            {
                return null;
            }

            return 0.2m / (decimal) duration?.TotalHours;
        }
    }


    class SprintTimer
    {
        SerialPort FDS { get; set; }

        readonly Channel<TimeEvent> pipeOut;

        public SprintTimer(Channel<TimeEvent> c, string port)
        {
            pipeOut = c;
            FDS = new SerialPort(port);
            FDS.DataReceived += new SerialDataReceivedEventHandler(FDS_DataReceived);
            try
            {
                FDS.Open();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public void close()
        {
            try
            {
                FDS.Close();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public void FDS_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var sp = (SerialPort)sender;
            try
            {
                var AlgeMessage = sp.ReadExisting().Split(' ').Where(
                    x => !string.IsNullOrWhiteSpace(x)).ToArray();

                //Channel can be c1m or c1
                var channel = AlgeMessage[1].Substring(1, 1);
                var manual = AlgeMessage[1].Length == 3 && AlgeMessage[1].Substring(2, 1) == "M";
                var timestamp = AlgeMessage[2];

                var ts = new TimeEvent
                {
                    Channel = int.Parse(channel),
                    Timestamp = TimeOnly.ParseExact(
                        timestamp, "HH:mm:ss.ffff"),
                    IsManual = manual
                };

                pipeOut.Writer.WriteAsync(ts);

            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    public record TimeEvent
    {
        public int Channel { get; init; }
        public TimeOnly Timestamp { get; init; }
        public bool IsManual { get; init; }
    }

}