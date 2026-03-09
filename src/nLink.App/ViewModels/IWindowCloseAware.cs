using System.Threading.Tasks;

namespace NLink.App.ViewModels;

internal interface IWindowCloseAware
{
    Task PrepareForWindowCloseAsync();
}
