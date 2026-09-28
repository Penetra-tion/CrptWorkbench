using Microsoft.AspNetCore.Components.Routing;

namespace CrptWorkbench.Web.Components.Layout;

public sealed class OperationsNavLink : NavLink
{
    protected override bool ShouldMatch(string currentUriAbsolute) =>
        OperationNavigation.IsOperationPath(new Uri(currentUriAbsolute).AbsolutePath);
}
