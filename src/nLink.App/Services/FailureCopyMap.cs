namespace NLink.App.Services;

public static class FailureCopyMap
{
    public static FailurePresentation For(TransportFailureCategory category)
        => FailurePresenter.Present(category);
}
