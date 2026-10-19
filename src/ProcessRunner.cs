using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexZhLauncher
{
    internal static class ProcessRunner
    {
        public static Task<ProcessResult> RunAsync(string fileName, string arguments)
        {
            return RunAsync(fileName, arguments, TimeSpan.Zero);
        }

        /// <summary>
        /// 跑一个进程并收集它的输出。
        /// timeout 为 <see cref="TimeSpan.Zero"/> 表示不设超时（沿用旧行为）；
        /// 否则超时即杀掉子进程并返回**失败结果**（不是抛异常）——
        /// 让调用方按普通失败处理，不会把异常冒到 UI。
        ///
        /// 为什么需要它：部分系统查询在 AppXSvc / Store 繁忙时会长时间不返回，
        /// 而调用方是 `await` 它的 —— 没有超时就会永久挂住，
        /// 表现为界面卡在「正在检测 Codex」且主按钮一直禁用。
        /// </summary>
        public static Task<ProcessResult> RunAsync(string fileName, string arguments, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource<ProcessResult>();
            var output = new StringBuilder();
            var error = new StringBuilder();
            var process = new Process();
            Timer watchdog = null;

            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null) output.AppendLine(args.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null) error.AppendLine(args.Data);
            };
            process.Exited += delegate
            {
                if (watchdog != null) { try { watchdog.Dispose(); } catch { } }
                process.WaitForExit();
                completion.TrySetResult(new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = output.ToString(),
                    Error = error.ToString()
                });
                process.Dispose();
            };

            try
            {
                if (!process.Start())
                {
                    completion.SetException(new InvalidOperationException("无法启动进程：" + fileName));
                }
                else
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (timeout > TimeSpan.Zero)
                    {
                        var seconds = (int)Math.Ceiling(timeout.TotalSeconds);
                        watchdog = new Timer(delegate
                        {
                            try { process.Kill(); } catch { }
                            // TrySetResult：若进程恰好同时正常退出，以先到者为准，
                            // 不会覆盖掉真实结果。
                            completion.TrySetResult(new ProcessResult
                            {
                                ExitCode = -1,
                                Output = output.ToString(),
                                Error = "进程超过 " + seconds + " 秒未返回，已终止：" + fileName
                            });
                        }, null, timeout, Timeout.InfiniteTimeSpan);
                    }
                }
            }
            catch (Exception ex)
            {
                process.Dispose();
                completion.SetException(ex);
            }

            return completion.Task;
        }

        public static string PowerShellLiteral(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "''") + "'";
        }

        public static string QuoteArgument(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}

