// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Document Permissions")]
    [Description("Set permissions for document operations")]
    public static class Documents
    {
        [Description("Allows viewing document details")]
        public const string View = "Permissions.Documents.View";

        [Description("Allows creating new document records")]
        public const string Create = "Permissions.Documents.Create";

        [Description("Allows modifying existing document details")]
        public const string Edit = "Permissions.Documents.Edit";

        [Description("Allows deleting document records")]
        public const string Delete = "Permissions.Documents.Delete";

        [Description("Allows downloading document files")]
        public const string Download = "Permissions.Documents.Download";

        [Description("Allows searching for document records")]
        public const string Search = "Permissions.Documents.Search";

        // Export and Import are deliberately absent. Neither capability exists: the Documents page
        // has no export or import control, and the two request types they were named for -
        // ExportDocumentsQuery and GetAllDocumentsQuery - were empty 3-byte files that declared no
        // type and were sent by nothing. A permission constant nothing can check is not harmless: it
        // appears in the role editor as a grantable right, so an administrator spends a decision on
        // a capability the application does not have. If document export is ever built, the
        // constants come back with it.
        //
        // Same removal, for the same reason, that Pass 11B/11C performed on Permissions.Logs.Export.
    }
}

public class DocumentsAccessRights
{
    public bool View { get; set; }
    public bool Create { get; set; }
    public bool Edit { get; set; }
    public bool Delete { get; set; }
    public bool Download { get; set; }
    public bool Search { get; set; }

    // No Export or Import, matching Permissions.Documents above. PermissionService builds the claim
    // string from the property NAME - "Permissions.Documents." + prop.Name - so a property left here
    // would go on manufacturing a claim string that no constant declares and no role can be granted.
    // The same pairing LogsAccessRights documents for its own missing Export.
}
