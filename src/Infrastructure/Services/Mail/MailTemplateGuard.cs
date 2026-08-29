// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using CleanArchitecture.Blazor.Application.Common.Constants;
using Scriban;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Mail;

/// <summary>
///     Proves, at startup, that every template the code can name is present and usable.
/// </summary>
/// <remarks>
///     Templates are files, and files go missing between a build and a deployment. When they do, the
///     failure surfaces at the worst possible moment - somebody's password reset - and in this
///     application it surfaces almost invisibly, because the send happens inside a notification
///     handler and the notification publisher swallows handler exceptions. MNEFleets lost six days
///     to exactly that shape.
///     <para>
///     <b>Presence is not enough, and this template set proves it.</b> These bodies contain U+2019 in
///     "Here's" and "didn't". A lossy re-encode - a copy through a tool that assumes Windows-1252, a
///     careless editor save - leaves a file that exists, has a plausible length, passes any
///     File.Exists check, and renders "Hereâ€™s" to every customer. So the guard checks four things,
///     each catching a different accident: the file EXISTS (deployment lost it), it DECODES as
///     strict UTF-8 (bytes corrupted in transit), it contains no U+FFFD (it was already mangled
///     before being re-saved, and decoded "successfully" into replacement characters), and it PARSES
///     (truncated or edited into invalid Scriban).
///     </para>
/// </remarks>
public static class MailTemplateGuard
{
    /// <summary>The replacement character a lossy decode leaves behind.</summary>
    private const char ReplacementCharacter = '�';

    /// <summary>
    ///     Checks every template in <see cref="MailTemplates.All" />.
    /// </summary>
    /// <returns>One human-readable problem per unusable template; empty when all are well.</returns>
    public static IReadOnlyList<string> Check() => MailTemplates.All.Select(Check)
        .Where(problem => problem is not null)
        .Select(problem => problem!)
        .ToList();

    /// <summary>Checks one template, returning null when it is usable.</summary>
    public static string? Check(string template)
    {
        var path = MailTemplateRenderer.PathFor(template);

        if (!File.Exists(path)) return $"Mail template '{template}' is missing: expected it at {path}.";

        string content;
        try
        {
            // throwOnInvalidBytes is the entire point of constructing an encoding by hand here.
            // File.ReadAllText's default UTF8 decoder REPLACES invalid bytes silently, which is the
            // behaviour that lets a mangled template pass for a good one.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            content = File.ReadAllText(path, strict);
        }
        catch (DecoderFallbackException ex)
        {
            return $"Mail template '{template}' at {path} is not valid UTF-8: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Mail template '{template}' at {path} could not be read: {ex.Message}";
        }

        // Reaching here with a replacement character means the bytes were already mangled before
        // this file was written - a re-save of a badly decoded original. Strict decoding cannot see
        // that, because U+FFFD is legitimately encodable.
        var index = content.IndexOf(ReplacementCharacter);
        if (index >= 0)
        {
            return $"Mail template '{template}' at {path} contains the Unicode replacement character " +
                   $"at position {index}, so its text has been corrupted by a lossy re-encode.";
        }

        var parsed = Template.Parse(content);
        if (parsed.HasErrors)
        {
            var errors = string.Join(", ", parsed.Messages.Select(m => m.Message));
            return $"Mail template '{template}' at {path} does not parse: {errors}";
        }

        return null;
    }
}
