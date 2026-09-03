// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    /// <summary>
    ///     Returns a list of Permissions by scanning all assemblies for Permissions classes.
    /// </summary>
    /// <returns></returns>
    public static List<string> GetRegisteredPermissions()
    {
        var permissions = new List<string>();
        
        // Scan current assembly for all classes named "Permissions" (both in Common and Features)
        var assembly = Assembly.GetExecutingAssembly();
        var permissionClasses = assembly.GetTypes()
            .Where(t => t.Name == "Permissions" && t.IsClass && t.IsAbstract && t.IsSealed)
            .ToList();

        foreach (var permissionClass in permissionClasses)
        {
            foreach (var nestedType in permissionClass.GetNestedTypes())
            {
                var fields = nestedType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                foreach (var field in fields)
                {
                    var propertyValue = field.GetValue(null);
                    if (propertyValue is string permission)
                        permissions.Add(permission);
                }
            }
        }

        return permissions.Distinct().ToList();
    }

    // Permissions.NavigationMenu was removed in Pass 26. It gated nothing, and had it been wired it
    // would have been a THIRD gating mechanism in front of destinations that are already protected
    // twice: the navigation menu filters its entries by ROLE (see MenuSectionItemModel.Roles and
    // AppLayout.razor, which passes the principal's assigned roles into the component), and every
    // destination behind it carries its own permission on its own page. A menu-level right would
    // have been weaker than both and consulted by neither.

    [DisplayName("Hangfire Permissions")]
    [Description("Set permissions for Hangfire dashboard")]
    public static class Hangfire
    {
        [Description("Allows viewing Hangfire dashboard")]
        public const string View = "Permissions.Hangfire.View";
    }
    [DisplayName("Email Templates Permissions")]
    [Description("Set permissions for Email Templates")]
    public static class EmailTemplates
    {
        [Description("Allows viewing Email Templates")]
        public const string View = "Permissions.EmailTemplates.View";
        
        [Description("Allows creating Email Templates")]
        public const string Create = "Permissions.EmailTemplates.Create";
        
        [Description("Allows editing Email Templates")]
        public const string Edit = "Permissions.EmailTemplates.Edit";
        
        [Description("Allows deleting Email Templates")]
        public const string Delete = "Permissions.EmailTemplates.Delete";
    }

} 
