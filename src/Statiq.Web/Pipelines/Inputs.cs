﻿using System;
using System.Collections.Generic;
using System.Linq;
using Statiq.Common;
using Statiq.Core;
using Statiq.Web.Modules;
using Statiq.Yaml;

namespace Statiq.Web.Pipelines
{
    public class Inputs : Pipeline
    {
        public Inputs(Templates templates)
        {
            // These are the media types that will have front matter processed,
            // generally the templates, scripts (.cs, .csx), and the special .statiq extension
            string[] mediaTypesToProcess = templates.Keys
                .Concat(new[] { MediaTypes.CSharp, MediaTypes.Statiq })
                .ToArray();

            Dependencies.Add(nameof(DirectoryMetadata));

            InputModules = new ModuleList
            {
                // Read files in one place so that the documents have a unified ID and we can apply metadata once
                // Also add headless CMS support or other input sources here (probably at the end of ProcessModules as a concat if they come over with metadata)
                // Always read *.statiq files no matter what (even if previous patterns would have excluded them)
                new ReadFiles(Config.FromSettings(settings =>
                    settings.GetList(WebKeys.InputFiles, Array.Empty<string>())
                        .Concat(settings.GetList(WebKeys.AdditionalInputFiles, Array.Empty<string>()))
                        .Concat("*.statiq")))
            };

            ProcessModules = new ModuleList
            {
                // Apply directory metadata
                new ExecuteIf(Config.FromSetting(WebKeys.ApplyDirectoryMetadata, true))
                {
                    new ApplyDirectoryMetadata()
                },

                // Apply side car files
                new ExecuteIf(Config.FromSetting(WebKeys.ProcessSidecarFiles, true))
                {
                    new ProcessSidecarFile(Config.FromDocument((doc, ctx) =>
                    {
                        NormalizedPath relativePath = doc.Source.IsNull ? NormalizedPath.Null : doc.Source.GetRelativeInputPath();
                        if (relativePath.IsNull)
                        {
                            return NormalizedPath.Null;
                        }
                        relativePath = relativePath.InsertPrefix("_");
                        return templates.GetMediaTypes(ContentType.Data, Phase.Process)
                            .SelectMany(MediaTypes.GetExtensions)
                            .SelectMany(x => new NormalizedPath[]
                            {
                                relativePath.ChangeExtension(x),
                                relativePath.AppendExtension(x)
                            })
                            .FirstOrDefault(x => ctx.FileSystem.GetInputFile(x).Exists);
                    }))
                    {
                        templates.GetModule(ContentType.Data, Phase.Process)
                    }
                },

                // Set media type from directory and sidecar files
                new ExecuteIf(Config.FromDocument(doc => doc.ContainsKey(WebKeys.MediaType)))
                {
                    new SetMediaType(Config.FromDocument(doc => doc.GetString(WebKeys.MediaType)))
                },

                // Process front matter for media types where we have an existing template (and scripts)
                new ExecuteIf(Config.FromDocument(doc => mediaTypesToProcess.Any(x => doc.MediaTypeEquals(x))))
                {
                    // Remove the file extension if this was a .statiq file
                    new ExecuteIf(Config.FromDocument(doc => doc.MediaTypeEquals(MediaTypes.Statiq)))
                    {
                        new SetDestination(Config.FromDocument(doc => doc.Source.GetRelativeInputPath().ChangeExtension(null))),
                    },

                    // Extract front matter
                    // TODO: accept other types of front matter based on first char (`<` = XML, `{` = JSON)
                    new ExtractFrontMatter(
                        Config.FromSettings<IEnumerable<string>>(settings =>
                        {
                            List<string> regexes = new List<string>();
                            regexes.AddRange(settings.GetList<string>(WebKeys.FrontMatterRegexes, Array.Empty<string>()));
                            regexes.AddRange(settings.GetList(WebKeys.AdditionalFrontMatterRegexes, Array.Empty<string>()));
                            return regexes;
                        }),
                        new ParseYaml()),

                    // Set new media type from metadata (in case front matter changed it)
                    new ExecuteIf(Config.FromDocument(doc => doc.ContainsKey(WebKeys.MediaType)))
                    {
                        new SetMediaType(Config.FromDocument(doc => doc.GetString(WebKeys.MediaType)))
                    },

                    // Set some standard metadata
                    new SetMetadata(WebKeys.Published, Config.FromDocument((doc, ctx) => doc.GetPublishedDate(ctx, ctx.GetBool(WebKeys.PublishedUsesLastModifiedDate)))),

                    // Enumerate metadata values (remove the enumerate key so following modules won't enumerate again)
                    new ExecuteIf(Config.FromDocument(doc => doc.ContainsKey(Keys.Enumerate)))
                    {
                        new EnumerateValues(),
                        new SetMetadata(Keys.Enumerate, (string)null)
                    },

                    // Evaluate scripts (but defer if the script is for an archive)
                    // Script return document should either have media type or content type set, or script metadata should have content type, otherwise script output will be treated as an asset
                    new ProcessScripts(true)
                },

                // Set content type based on media type if not already explicitly set as metadata
                new SetContentType(templates),

                // If it's data, go ahead and process the data content (which might actually change the content type to something else
                // This also lets us filter out feeds in the data pipeline without evaluating the data twice
                new ExecuteIf(Config.FromDocument(doc => doc.Get<ContentType>(WebKeys.ContentType) == ContentType.Data))
                {
                    templates.GetModule(ContentType.Data, Phase.Process),
                },

                // Enumerate values one more time in case data content or the script output some (remove the enumerate key so following modules won't enumerate again)
                new ExecuteIf(Config.FromDocument(doc => doc.ContainsKey(Keys.Enumerate)))
                {
                    new EnumerateValues(),
                    new SetMetadata(Keys.Enumerate, (string)null)
                },

                // Filter out excluded documents
                new FilterDocuments(Config.FromDocument(doc => !doc.GetBool(WebKeys.Excluded))),
            };
        }
    }
}