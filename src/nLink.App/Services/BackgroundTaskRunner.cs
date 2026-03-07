using System;
using System.Threading.Tasks;
using NLink.Core.Logging;

namespace NLink.App.Services;

internal static class BackgroundTaskRunner
{
    public static Task Run(
        Func<Task> body,
        string source,
        string operationName,
        Action? onFinally = null,
        Func<string?>? contextProvider = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "BackgroundTask" : source.Trim();
        var normalizedOperation = string.IsNullOrWhiteSpace(operationName) ? "background_task" : operationName.Trim();

        return Task.Run(async () =>
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected during normal stop/reset paths.
            }
            catch (Exception ex)
            {
                var context = TryGetContext(contextProvider);
                var suffix = string.IsNullOrWhiteSpace(context) ? string.Empty : $"; {context}";
                LocalOperationalLog.Warn(
                    normalizedSource,
                    $"event=background_task_failed; op={normalizedOperation}; ex={ex.GetType().Name}{suffix}");
            }
            finally
            {
                try
                {
                    onFinally?.Invoke();
                }
                catch (Exception ex)
                {
                    LocalOperationalLog.Warn(
                        normalizedSource,
                        $"event=background_task_cleanup_failed; op={normalizedOperation}; ex={ex.GetType().Name}");
                }
            }
        });
    }

    private static string? TryGetContext(Func<string?>? contextProvider)
    {
        if (contextProvider is null)
        {
            return null;
        }

        try
        {
            return contextProvider();
        }
        catch (Exception ex)
        {
            return $"context_error={ex.GetType().Name}";
        }
    }
}
