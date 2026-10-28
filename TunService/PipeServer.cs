using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal sealed class PipeServer(TunEngine engine)
{
    private readonly SecurityIdentifier _user = new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "client.sid")).Trim());
    public async Task RunAsync(CancellationToken cancellationToken, Action? ready = null)
    {
        ProcessSecurity.AllowQuery(_user);
        var clients = new ConcurrentDictionary<int, Task>(); int sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(_user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(TunProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                ready?.Invoke(); ready = null;
                try { await pipe.WaitForConnectionAsync(cancellationToken); }
                catch { pipe.Dispose(); throw; }
                int number = ++sequence;
                Task client = HandleAsync(pipe, cancellationToken);
                clients[number] = client;
                _ = client.ContinueWith(_ => clients.TryRemove(number, out Task? removed), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        finally { await Task.WhenAll(clients.Values); await engine.StopAsync(); }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(TunProtocol.ServiceRequestSeconds));
            try
            {
                TunRequest request = await TunProtocol.ReadAsync<TunRequest>(pipe, timeout.Token);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0) return;
                bool authorized = false;
                pipe.RunAsClient(() => { using WindowsIdentity identity = WindowsIdentity.GetCurrent(); authorized = identity.User?.Equals(_user) == true; });
                if (!authorized) return;
                TunResponse response = await engine.ExecuteAsync(request, checked((int)pid), timeout.Token);
                await TunProtocol.WriteAsync(pipe, response, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
            catch { try { await engine.StopAsync(); } catch { } }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
