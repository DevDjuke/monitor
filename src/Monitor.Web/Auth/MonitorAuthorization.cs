using Microsoft.AspNetCore.Authorization;

namespace Monitor.Web.Auth;

public static class MonitorRoles
{
    public const string Owner = "Owner";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string Auditor = "Auditor";

    public static readonly IReadOnlyList<string> All = [Owner, Operator, Viewer, Auditor];
}

public static class MonitorPolicies
{
    public const string View = "Monitor.View";
    public const string Audit = "Monitor.Audit";
    public const string Configure = "Monitor.Configure";
    public const string Control = "Monitor.Control";
    public const string ManageOperators = "Monitor.ManageOperators";

    public static void ConfigurePolicies(AuthorizationOptions options)
    {
        options.AddPolicy(View, policy =>
            policy.RequireRole(MonitorRoles.Owner, MonitorRoles.Operator, MonitorRoles.Viewer, MonitorRoles.Auditor));
        options.AddPolicy(Audit, policy =>
            policy.RequireRole(MonitorRoles.Owner, MonitorRoles.Auditor));
        options.AddPolicy(Configure, policy =>
            policy.RequireRole(MonitorRoles.Owner, MonitorRoles.Operator));
        options.AddPolicy(Control, policy =>
            policy.RequireRole(MonitorRoles.Owner, MonitorRoles.Operator));
        options.AddPolicy(ManageOperators, policy =>
            policy.RequireRole(MonitorRoles.Owner));
    }
}
