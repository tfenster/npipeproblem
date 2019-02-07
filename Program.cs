using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace npipeproblem
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("use http, don't sleep between commands");
            MainAsync(true, false).Wait(); // --> works

            Console.WriteLine("use http, sleep between commands");
            MainAsync(true, true).Wait(); // --> works

            Console.WriteLine("use npipe, don't sleep between commands");
            MainAsync(false, false).Wait(); // --> works

            Console.WriteLine("use npipe, sleep between commands");
            MainAsync(false, true).Wait(); // --> hangs
        }

        static async Task MainAsync(bool useHttp, bool sleep)
        {
            var uri = new Uri("npipe://./pipe/docker_engine");
            if (useHttp) uri = new Uri("http://localhost:2375");

            using (var client = new DockerClientConfiguration(uri).CreateClient())
            {
                // create container
                var container = await client.Containers.CreateContainerAsync(
                    new CreateContainerParameters
                    {
                        Image = "mcr.microsoft.com/powershell:nanoserver-1809",
                        Cmd = new string[] { "ping", "-t", "localhost" }
                    }
                );

                // start container
                if (await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters()))
                {
                    // create exec
                    var exec = await client.Containers.ExecCreateContainerAsync(container.ID, new Docker.DotNet.Models.ContainerExecCreateParameters()
                        {
                            AttachStderr = true,
                            AttachStdin = true,
                            AttachStdout = true,
                            Cmd = new string[] { "pwsh" },
                            Detach = false,
                            Tty = false
                        });
                    
                    // start exec and attach
                    using (var stream = await client.Containers.StartAndAttachContainerExecAsync(exec.ID, false, default(CancellationToken)))
                    {
                        // show output
                        var tRead = Task.Run(async () =>
                        {
                            var dockerBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                            try
                            {
                                while (true)
                                {
                                    Array.Clear(dockerBuffer, 0, dockerBuffer.Length);
                                    var dockerReadResult = await stream.ReadOutputAsync(dockerBuffer, 0, dockerBuffer.Length, default(CancellationToken));

                                    if (dockerReadResult.EOF)
                                        break;
                                    
                                    if (dockerReadResult.Count > 0)
                                    {
                                        Console.WriteLine(Encoding.ASCII.GetString(new ArraySegment<byte>(dockerBuffer, 0, dockerReadResult.Count)));
                                    }
                                    else
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Failure during Read from Docker Exec: " + ex.Message);
                            }
                            System.Buffers.ArrayPool<byte>.Shared.Return(dockerBuffer);
                        });

                        // run test commands
                        var tWrite = Task.Run(async () =>
                        {
                            try {
                                Console.WriteLine("### send gci");
                                var cmd = Encoding.ASCII.GetBytes($"Get-ChildItem{Environment.NewLine}");
                                await stream.WriteAsync(cmd, 0, cmd.Length, default(CancellationToken));

                                if (sleep) {
                                    Console.WriteLine("### sleep");
                                    Thread.Sleep(5 * 1000);
                                }

                                Console.WriteLine("### send mkdir");
                                cmd = Encoding.ASCII.GetBytes($"mkdir temp{Environment.NewLine}");
                                await stream.WriteAsync(cmd, 0, cmd.Length, default(CancellationToken));

                                if (sleep) {
                                    Console.WriteLine("### sleep");
                                    Thread.Sleep(5 * 1000);
                                }

                                Console.WriteLine("### send gci");
                                cmd = Encoding.ASCII.GetBytes($"Get-ChildItem{Environment.NewLine}");
                                await stream.WriteAsync(cmd, 0, cmd.Length, default(CancellationToken));

                                Console.WriteLine("### always sleep before kill if not npipe");
                                if (useHttp) Thread.Sleep(5 * 1000);

                                Console.WriteLine("### send kill");
                                cmd = Encoding.ASCII.GetBytes($"Get-Process ping | Stop-Process{Environment.NewLine}");
                                await stream.WriteAsync(cmd, 0, cmd.Length, default(CancellationToken));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Failure during Write to Docker Exec: " + ex.Message);
                            }
                        });

                        await tRead;
                        await tWrite;

                        await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters() { Force = true } , default(CancellationToken));
                    }

                    Console.WriteLine("### done");
                }
            }
        }
    }
}
