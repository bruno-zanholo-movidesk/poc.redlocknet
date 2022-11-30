using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace poc.redlocknet
{
    internal class Program
    {
        static string GenerateLockKey(string input) => $"lock-key-{input}";

        static void Main(string[] args)
        {
            var random = new Random();
            var appSettings = ConfigurationManager.AppSettings;

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
                                var seconds = random.Next(
                                    Convert.ToInt16(appSettings["MinSecondsToWait"]),
                                    Convert.ToInt16(appSettings["MaxSecondsToWait"]));

                                Sleep(redLock?.Resource, seconds);

                                if (input == "1" || input == "11")
                                    throw new Exception("Exception generate by input \"1\"");

                                poc.Release(redLock);
                            }
                            else
                            {
                                Console.WriteLine($"Lock can't be acquired");
                                Console.ReadKey();
                            }
                        }

                        input = GetInput();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: " + ex.Message);
                        Console.ReadKey();

                        if (redLock != null && input == "11")
                            poc.Release(redLock);

                        input = GetInput();
                    }
                }
            }

            Console.WriteLine("Bye...");
        }

        private static string GetInput()
        {
            Console.Clear();
            Console.WriteLine("Write \"Message-id\" or \"E\" to exit");

            var input = Console.ReadLine().Trim();

            Console.Clear();

            return input;
        }

        private static void Sleep(string resource, int seconds)
        {
            var x = seconds;

            while (x > 0)
            {
                Console.Clear();
                Console.WriteLine($"Process locked by resource {resource}");
                Console.WriteLine($"Waiting {x--} seconds in processing...");
                Thread.Sleep(1000);
            }
        }
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

        public IRedLock Lock(string resource, CancellationToken? cancellationToken = null)
        {
            var settings = Seetings();
            Console.WriteLine($"Creating lock {resource}");
            var redlock = RedLockFactory.CreateLock(resource, settings.ExpiryTime, settings.WaitTime, settings.RetryTime, cancellationToken ?? CancellationToken.None);
            Console.WriteLine($"Lock created {resource}");
            return redlock;
        }

        public async Task<IRedLock> LockAsync(string resource, CancellationToken? cancellationToken = null)
        {
            var settings = Seetings();
            Console.WriteLine("Creating lock");
            var redlock = await RedLockFactory.CreateLockAsync(resource, settings.ExpiryTime, settings.WaitTime, settings.RetryTime, cancellationToken ?? CancellationToken.None);
            Console.WriteLine("Lock created");
            return redlock;
        }

        public void Release(IRedLock redLock)
        {
            Console.WriteLine($"Releasing lock {redLock.Resource}");
            redLock.Dispose();
            Console.WriteLine($"Lock released {redLock.Resource}");
        }

        private static (TimeSpan ExpiryTime, TimeSpan WaitTime, TimeSpan RetryTime) Seetings()
        {
            var appSettings = ConfigurationManager.AppSettings;
            return (TimeSpan.FromSeconds(Convert.ToInt32(appSettings["ExpiryTime"])), TimeSpan.FromSeconds(Convert.ToInt32(appSettings["WaitTime"])), TimeSpan.FromSeconds(Convert.ToInt32(appSettings["RetryTime"])));
        }

        public void Dispose()
        {
            RedLockFactory?.Dispose();
            Connection?.Dispose();
        }
    }
}
