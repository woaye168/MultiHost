using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiHost;

public record CommandReply(int Code, object? Data = null, string? Error = null);
public sealed class GuiPipe : IDisposable
{
    public static string Name
    {
        get
        {
            var workspace = Environment.GetEnvironmentVariable("MULTIHOST_WORKSPACE");
            var suffix = string.IsNullOrWhiteSpace(workspace) ? "" : "." + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace).TrimEnd('\\', '/').ToUpperInvariant())))[..16];
            return "MultiHost.v3." + WindowsIdentity.GetCurrent().User!.Value + suffix;
        }
    }
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(16);
    private readonly MainWindow _window;
    public GuiPipe(MainWindow window) { _window = window; _ = ListenAsync(); }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            bool reserved = false;
            try
            {
                await _connections.WaitAsync(_stop.Token);
                reserved = true;
                pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                _ = HandleClientAsync(pipe);
                pipe = null; // Handler owns the connected pipe and connection slot.
                reserved = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.Log($"CLI 通道: {ex.Message}");
                if (!_stop.IsCancellationRequested)
                    try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { }
            }
            finally
            {
                pipe?.Dispose();
                if (reserved) _connections.Release();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line == null) return;
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                CommandReply reply;
                try
                {
                    var args = JsonSerializer.Deserialize<string[]>(line);
                    if (args == null || args.Any(a => a == null)) throw new ArgumentException("命令必须是字符串数组");
                    var operation = await _window.Dispatcher.InvokeAsync(() => _window.ExecuteCommandAsync(args));
                    reply = await operation;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { reply = new(2, Error: ex.Message); }
                catch (Exception ex) { reply = new(1, Error: ex.Message); }
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) LogService.Log($"CLI 连接: {ex.Message}"); }
        finally { _connections.Release(); }
    }

    public void Dispose() => _stop.Cancel();

    public static async Task<CommandReply> SendAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(args).AsMemory(), timeout.Token);
        return JsonSerializer.Deserialize<CommandReply>(await reader.ReadLineAsync(timeout.Token) ?? "null")
            ?? new(1, Error: "GUI 未返回结果");
    }
}
