using System.Threading.Tasks;

namespace NLink.App.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}
