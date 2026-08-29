// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Constants;
using Scriban;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Mail;

/// <summary>
///     Turns a template name and a model into an HTML body.
/// </summary>
/// <remarks>
///     Separate from the transports so that both of them - Mailgun and the development sink - render
///     identically. A sink that rendered differently from the real sender would be worse than no
///     sink at all.
/// </remarks>
public sealed class MailTemplateRenderer(
    IApplicationSettings applicationSettings,
    ILogger<MailTemplateRenderer> logger)
{
    /// <summary>The four tokens supplied to every template whether the caller asks or not.</summary>
    public const string UserNameToken = "user_name";
    public const string AppNameToken = "app_name";
    public const string CompanyToken = "company";
    public const string BaseUrlToken = "base_url";

    /// <summary>
    ///     Where templates live, resolved against the application's base directory.
    /// </summary>
    /// <remarks>
    ///     <see cref="AppContext.BaseDirectory"/>, not <c>Directory.GetCurrentDirectory()</c>. The
    ///     old implementation used the working directory, which happens to equal the output
    ///     directory when the host is launched the usual way and does not when it is started as a
    ///     service or from a different folder - at which point no template is ever found and every
    ///     email fails.
    /// </remarks>
    public static string PathFor(string template) =>
        Path.Combine(AppContext.BaseDirectory, MailTemplates.RelativePath(template));

    /// <summary>
    ///     Renders a template, or returns why it could not be rendered.
    /// </summary>
    public async Task<Result<string>> RenderAsync(
        MailRecipient to, string template, object? model, CancellationToken cancellationToken = default)
    {
        var path = PathFor(template);

        if (!File.Exists(path))
        {
            logger.LogError("Mail template {Template} not found at {Path}", template, path);
            return await Result<string>.FailureAsync($"Mail template '{template}' was not found at {path}.");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail template {Template} could not be read from {Path}", template, path);
            return await Result<string>.FailureAsync($"Mail template '{template}' could not be read: {ex.Message}");
        }

        var parsed = Template.Parse(content);
        if (parsed.HasErrors)
        {
            var errors = string.Join(", ", parsed.Messages.Select(m => m.Message));
            logger.LogError("Mail template {Template} failed to parse: {Errors}", template, errors);
            return await Result<string>.FailureAsync($"Mail template '{template}' failed to parse: {errors}");
        }

        try
        {
            var body = await parsed.RenderAsync(BuildModel(to, model));
            return await Result<string>.SuccessAsync(body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail template {Template} failed to render", template);
            return await Result<string>.FailureAsync($"Mail template '{template}' failed to render: {ex.Message}");
        }
    }

    /// <summary>
    ///     The model Scriban sees: the caller's tokens, plus the four supplied centrally.
    /// </summary>
    /// <remarks>
    ///     <b>Caller wins.</b> The four are defaults, not overrides - a handler that deliberately
    ///     sets <c>company</c> to something other than the configured company keeps its value. They
    ///     are injected because the alternative is what this template used to do and what MNEFleets
    ///     did before it: every handler assembling <c>app_name</c> and <c>company</c> by hand, so
    ///     that one handler written without them renders a mail with blanks where the product name
    ///     should be, and nobody notices until a customer says so.
    ///     <para>
    ///     <c>base_url</c> ships even though no template uses it today. A background-originated
    ///     email has no <c>HttpContext</c> to build an absolute link from, so the moment anyone adds
    ///     one they need this; providing it now costs a dictionary entry.
    ///     </para>
    /// </remarks>
    private Dictionary<string, object?> BuildModel(MailRecipient to, object? model)
    {
        var result = ToScribanModel(model);

        // TryAdd, not assignment: present keys are the caller's and stay the caller's.
        result.TryAdd(UserNameToken, to.Greeting);
        result.TryAdd(AppNameToken, applicationSettings.AppName);
        result.TryAdd(CompanyToken, applicationSettings.Company);
        result.TryAdd(BaseUrlToken, applicationSettings.ApplicationUrl);

        return result;
    }

    /// <summary>
    ///     Converts a model's PascalCase properties to the snake_case names the templates use.
    /// </summary>
    /// <remarks>
    ///     <b>A dictionary is passed straight through, never reflected over.</b> Reflection on a
    ///     <see cref="Dictionary{TKey,TValue}"/> walks the dictionary's OWN properties - Count, Keys,
    ///     Values, Comparer - producing a model with those four names in it and not one of the
    ///     tokens the caller prepared, so every real token renders empty. The template still renders,
    ///     the email still sends, and every value in it is blank.
    /// </remarks>
    public static Dictionary<string, object?> ToScribanModel(object? model)
    {
        if (model is null) return new Dictionary<string, object?>();

        if (model is IDictionary<string, object?> prepared) return new Dictionary<string, object?>(prepared);

        var result = new Dictionary<string, object?>();
        foreach (var property in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            result[ToSnakeCase(property.Name)] = property.GetValue(model);
        }

        return result;
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
