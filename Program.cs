using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace poc.redlocknet
{
    internal class Program
    {
        static string GenerateLockKey(string input) => $"lock-key-{input}";

        static void Main(string[] args)
        {
            var random = new Random();
            var appSettings = ConfigurationManager.AppSettings;

            var max = random.Next(Convert.ToInt32(appSettings["MinParallelTasks"]), Convert.ToInt32(appSettings["MaxParallelTasks"]));

            WriteLine($"Generating list of inputs with {max} distinct itens");

            var inputs = new List<KeyValuePair<int, bool>>();

            for (int i = 1; i < max + 1; i++)
            {
                inputs.Add(new KeyValuePair<int, bool>(i, false));
                inputs.Add(new KeyValuePair<int, bool>(i, true));

                if (i % 2 == 0)
                    inputs.Add(new KeyValuePair<int, bool>(i, true));

                if (i % 5 == 0)
                {
                    inputs.Add(new KeyValuePair<int, bool>(i, true));
                    inputs.Add(new KeyValuePair<int, bool>(i, true));
                }
            }

            WriteLine($"Using with {inputs.Count} itens");

            var acquired = 0;
            var notAcquired = 0;

            var maxDegreeOfParallelism = Convert.ToInt32(appSettings["MaxDegreeOfParallelism"]);
            var minSecondsToWait = Convert.ToInt16(appSettings["MinSecondsToWait"]);
            var maxSecondsToWait = Convert.ToInt16(appSettings["MaxSecondsToWait"]);

            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (var poc = new Poc())
            {
                Parallel.ForEach(inputs, new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism
                },
                (input) =>
                {
                    var lockKey = GenerateLockKey(input.Key.ToString());

                    IRedLock redLock = null;

                    try
                    {
                        using (redLock = poc.Lock(lockKey))
                        {
                            if (redLock.IsAcquired)
                            {                                
                                Sleep(lockKey, random.Next(minSecondsToWait, maxSecondsToWait), print: false, clear: false);

                                Interlocked.Increment(ref acquired);
                            }
                            else
                            {
                                WriteLine($"Lock can't be acquired for {lockKey}-{input.Value}");
                                Interlocked.Increment(ref notAcquired);
                            }
                        }
                        //WriteLine($"Process unlocked by resource {lockKey}");
                    }
                    catch (Exception ex)
                    {
                        WriteLine("Error: " + ex.Message);
                        poc.Release(redLock);
                    }
                });
            }

            sw.Stop();

            WriteLine($"Acquired: {acquired}");
            WriteLine($"Not acquired: {notAcquired}");
            WriteLine($"Time elapsed: {sw.Elapsed.Hours.ToString().PadLeft(2, '0')}:{sw.Elapsed.Minutes.ToString().PadLeft(2, '0')}:{sw.Elapsed.Seconds.ToString().PadLeft(2, '0')}.{sw.Elapsed.Milliseconds}");
            WriteLine($"Average: {inputs.Count / (sw.Elapsed.Seconds * 1M)} requests/seconds");

            Console.ReadKey();
            WriteLine("Bye...");
        }

        static void Main2(string[] args)
        {
            var random = new Random();
            var appSettings = ConfigurationManager.AppSettings;

            var minSecondsToWait = Convert.ToInt16(appSettings["MinSecondsToWait"]);
            var maxSecondsToWait = Convert.ToInt16(appSettings["MaxSecondsToWait"]);

            using (var poc = new Poc())
            {
                var input = GetInput();

                while (input.ToLower() != "e")
                {
                    IRedLock redLock = null;

                    try
                    {
                        if (!string.IsNullOrEmpty(input))
                        {
                            redLock = poc.Lock(GenerateLockKey(input));

                            if (redLock.IsAcquired)
                            {                                
                                Sleep(redLock?.Resource, random.Next(minSecondsToWait, maxSecondsToWait));

                                if (input == "1" || input == "11")
                                    throw new Exception("Exception generate by input \"1\"");

                                poc.Release(redLock);
                            }
                            else
                            {
                                WriteLine($"Lock can't be acquired for {redLock.Resource}");
                                Console.ReadKey();
                            }
                        }

                        input = GetInput();
                    }
                    catch (Exception ex)
                    {
                        WriteLine("Error: " + ex.Message);
                        Console.ReadKey();

                        if (redLock != null && input == "11")
                            poc.Release(redLock);

                        input = GetInput();
                    }
                }
            }

            WriteLine("Bye...");
        }

        private static string GetInput()
        {
            Console.Clear();
            WriteLine("Write \"Message-id\" or \"E\" to exit");

            var input = Console.ReadLine().Trim();
            
            return input;
        }

        private static void Sleep(string resource, int seconds, bool print = true, bool clear = true)
        {         
            if (print)
            {                
                WriteLine($"Process locked by resource {resource}");

                while (seconds > 0)
                {
                    if (clear) Console.Clear();
                    if (print)
                    {
                        WriteLine($"processing resource {resource} - waiting {seconds} seconds...");
                    }
                    seconds--;
                    Thread.Sleep(1000);
                }
            }
            else
                Thread.Sleep(seconds * 1000);
        }

        private static void WriteLine(string message) => Console.WriteLine($"[Poc.{Process.GetCurrentProcess().Id}] {message}");
    }

    internal class Poc : IDisposable
    {
        public IConnectionMultiplexer Connection { get; }
        public RedLockFactory RedLockFactory { get; }

        public Poc()
        {
            try
            {
                Console.WriteLine("Connecting to Redis...");

                var appSettings = ConfigurationManager.AppSettings;

                var configurationOptions = new ConfigurationOptions
                {
                    ClientName = $"Poc.{Process.GetCurrentProcess().Id}",
                    AbortOnConnectFail = false,
                    ConnectTimeout = Convert.ToInt32(appSettings["ConnectTimeout"]),
                    SyncTimeout = Convert.ToInt32(appSettings["CommandTimeout"]),
                    AsyncTimeout = Convert.ToInt32(appSettings["CommandTimeout"]),
                    DefaultDatabase = 1,
                    ReconnectRetryPolicy = new LinearRetry(Convert.ToInt32(appSettings["ConnectTimeout"])),
                    KeepAlive = 10
                };

                var urls = appSettings["Urls"];

                foreach (var url in urls.Split(';'))
                {
                    configurationOptions.EndPoints.Add(url);
                }

                Connection = ConnectionMultiplexer.Connect(configurationOptions);

                RedLockFactory = RedLockFactory.Create(new List<RedLockMultiplexer>
                {
                    new RedLockMultiplexer(Connection)
                });

                Console.WriteLine("Connected to Redis...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error on connection to Redis {ex.Message}");
                Console.ReadKey();
            }
        }

        public IRedLock Lock(string resource, CancellationToken cancellationToken = default)
        {
            var settings = Seetings();
            var redlock = RedLockFactory.CreateLock(resource, settings.ExpiryTime, settings.WaitTime, settings.RetryTime, cancellationToken);
            return redlock;
        }

        public async Task<IRedLock> LockAsync(string resource, CancellationToken cancellationToken = default)
        {
            var settings = Seetings();
            var redlock = await RedLockFactory.CreateLockAsync(resource, settings.ExpiryTime, settings.WaitTime, settings.RetryTime, cancellationToken);
            return redlock;
        }

        public void Release(IRedLock redLock)
        {
            redLock?.Dispose();
        }

        private static (TimeSpan ExpiryTime, TimeSpan WaitTime, TimeSpan RetryTime) Seetings()
        {
            var appSettings = ConfigurationManager.AppSettings;
            return (TimeSpan.FromMilliseconds(Convert.ToInt32(appSettings["ExpiryTime"])), TimeSpan.FromMilliseconds(Convert.ToInt32(appSettings["WaitTime"])), TimeSpan.FromMilliseconds(Convert.ToInt32(appSettings["RetryTime"])));
        }

        public void Dispose()
        {
            RedLockFactory?.Dispose();
            Connection?.Dispose();
        }
    }
}
