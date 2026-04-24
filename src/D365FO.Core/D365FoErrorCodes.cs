namespace D365FO.Core;

/// <summary>
/// Canonical machine-readable error codes used in <see cref="ToolResult{T}.Fail(string, string, string?)"/>.
/// These are part of the CLI / MCP public contract — JSON consumers switch on them.
/// New error codes should be added here and referenced by name rather than
/// string-literal throughout the codebase.
/// </summary>
public static class D365FoErrorCodes
{
    // Input / generic
    public const string BadInput = "BAD_INPUT";
    public const string InvalidArgs = "INVALID_ARGS";
    public const string InvalidRange = "INVALID_RANGE";
    public const string Unhandled = "UNHANDLED";
    public const string HandlerThrew = "HANDLER_THREW";
    public const string WriteFailed = "WRITE_FAILED";
    public const string SourceUnreadable = "SOURCE_UNREADABLE";

    // Index / metadata not found
    public const string TableNotFound = "TABLE_NOT_FOUND";
    public const string EdtNotFound = "EDT_NOT_FOUND";
    public const string ClassNotFound = "CLASS_NOT_FOUND";
    public const string EnumNotFound = "ENUM_NOT_FOUND";
    public const string MenuItemNotFound = "MENU_ITEM_NOT_FOUND";
    public const string FormNotFound = "FORM_NOT_FOUND";
    public const string QueryNotFound = "QUERY_NOT_FOUND";
    public const string ViewNotFound = "VIEW_NOT_FOUND";
    public const string EntityNotFound = "ENTITY_NOT_FOUND";
    public const string ReportNotFound = "REPORT_NOT_FOUND";
    public const string ServiceNotFound = "SERVICE_NOT_FOUND";
    public const string ServiceGroupNotFound = "SERVICE_GROUP_NOT_FOUND";
    public const string RoleNotFound = "ROLE_NOT_FOUND";
    public const string DutyNotFound = "DUTY_NOT_FOUND";
    public const string PrivilegeNotFound = "PRIVILEGE_NOT_FOUND";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string LabelNotFound = "LABEL_NOT_FOUND";
    public const string MethodNotFound = "METHOD_NOT_FOUND";

    // Filesystem / environment
    public const string PackagesPathMissing = "PACKAGES_PATH_MISSING";
    public const string PackagesPathNotFound = "PACKAGES_PATH_NOT_FOUND";

    // Build / SDLC
    public const string BuildFailed = "BUILD_FAILED";
    public const string SyncFailed = "SYNC_FAILED";
    public const string TestsFailed = "TESTS_FAILED";
    public const string BpFailed = "BP_FAILED";
    public const string GitFailed = "GIT_FAILED";
    public const string DoctorFailed = "DOCTOR_FAILED";

    // Daemon
    public const string DaemonNotRunning = "DAEMON_NOT_RUNNING";
    public const string DaemonPidCorrupt = "DAEMON_PID_CORRUPT";

    // MCP dispatcher
    public const string UnknownTool = "UNKNOWN_TOOL";
}
