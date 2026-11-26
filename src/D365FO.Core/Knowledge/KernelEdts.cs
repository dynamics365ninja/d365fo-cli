namespace D365FO.Core.Knowledge;

/// <summary>
/// Extended data types the X++ runtime defines, with no AOT element: <c>RecId</c>, <c>UserId</c>,
/// <c>ValidFromDateTime</c> and the rest. Shipped tables use them thousands of times and standard
/// EDTs extend them, but there is no <c>AxEdt</c> file for any, so the index cannot know them and
/// "not found" says nothing about whether the name compiles.
/// </summary>
/// <remarks>
/// The list is upstream's census (<c>src/knowledge/kernelEdts.ts</c>): every field EDT and EDT
/// parent in a shipped installation that has no <c>AxEdt</c> file and that the metadata provider
/// does not know either. The value is the field base type every shipped use agrees on; null where
/// no shipped field uses the type.
/// </remarks>
public static class KernelEdts
{
    private static readonly Dictionary<string, string?> BaseTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DataAreaId"] = "String",
            ["SelectableDataArea"] = "String",
            ["UserId"] = "String",
            ["RecId"] = "Int64",
            ["ValidFromDateTime"] = "UtcDateTime",
            ["ValidToDateTime"] = "UtcDateTime",
            ["ValidFromDate"] = "Date",
            ["ValidToDate"] = "Date",
            ["TableName"] = "String",
            ["FieldName"] = "String",
            ["CreatedDateTime"] = "UtcDateTime",
            ["RelationType"] = "Int64",
            ["FieldId"] = "Integer",
            ["TimeOfDay"] = "Time",
            ["ClassName"] = "String",
            ["ClassId"] = "Integer",
            ["TableId"] = "Integer",
            ["IdentifierName"] = "String",
            ["SecurityRoleAotName"] = "String",
            ["ModifiedDateTime"] = "UtcDateTime",
            ["SecurityRoleName"] = "String",
            ["CreatedBy"] = "String",
            ["EnumName"] = "String",
            ["SysRowVersionNumber"] = "Int64",
            ["CreatedTransactionId"] = "Int64",
            ["NetworkAlias"] = "String",
            ["ExtendedTypeId"] = "Integer",
            ["PartitionKey"] = "String",
            ["UtilElementName"] = "String",
            ["UserIdStr"] = "String",
            ["MethodName"] = "String",
            ["RecVersion"] = "Integer",
            ["InstalledLanguageId"] = "String",
            ["NetworkDomain"] = "String",
            ["ModifiedBy"] = "String",
            ["FormName"] = "String",
            ["QueryName"] = "String",
            ["SequenceNum"] = "Int64",
            ["EnumId"] = "Integer",
            ["SecurityDutyName"] = "String",
            ["ConfigurationKeyName"] = "String",
            ["SecurityDutyIdentifier"] = "String",
            ["MenuItemName"] = "String",
            ["ReportName"] = "String",
            ["ResourceName"] = "String",
            ["ConfigurationKeyId"] = "Integer",
            ["ExtendedDataTypeName"] = "String",
            ["Partition"] = "Int64",
            ["MoneyMST"] = "Real",
            ["UtilElementId"] = "Integer",
            ["Sid"] = "String",
            ["SecurityPrivilegeName"] = "String",
            ["SecurityPrivilegeIdentifier"] = "String",
            ["SecurableName"] = "String",
            ["SecurityRoleDescription"] = "String",
            ["MetadataXmlObject"] = "String",
            ["PreferredLocale"] = "String",
            ["WorkflowCategoryName"] = "String",
            ["MenuItemNameDisplay"] = "String",
            ["WorkflowTypeName"] = "String",
            ["LicenseCodeId"] = "Integer",
            ["SegregationOfDutiesRuleName"] = "String",
            ["IndexName"] = "String",
            ["RowNumber"] = "Integer",
            ["ModelName"] = "String",
            ["WebUrlItemName"] = "String",
            ["MenuItemNameAction"] = "String",
            ["DeploymentRecId"] = "Int64",
            ["MenuItemNameOutput"] = "String",
            ["ServiceName"] = "String",
            ["TableCollectionName"] = "String",
            ["ModelElementRecid"] = "Int64",
            ["SecurityRisk"] = "String",
            ["SecurityMitigation"] = "String",
            ["SegregationOfDutiesOverrideComment"] = "String",
            ["TitleFields"] = "String",
            ["GlobalObjectCacheScope"] = "String",
            ["WorkflowApprovalName"] = "String",
            ["Money"] = "Real",
            ["AssemblyName"] = "String",
            ["SecurityDutyDescription"] = "String",
            ["SegmentedEntryField"] = null,
            ["Sha1HashCode"] = "Container",
        };

    /// <summary>Every kernel EDT.</summary>
    public static IReadOnlyCollection<string> Names => BaseTypes.Keys;

    /// <summary>True when the runtime defines this EDT although no AOT element does.</summary>
    public static bool Contains(string? name) => name is not null && BaseTypes.ContainsKey(name.Trim());

    /// <summary>The field base type of a kernel EDT, or null when it is none or has no known one.</summary>
    public static string? BaseType(string? name)
        => name is not null && BaseTypes.TryGetValue(name.Trim(), out var type) ? type : null;
}
