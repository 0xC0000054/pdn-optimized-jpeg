/////////////////////////////////////////////////////////////////////////////////
//
// Optimized JPEG FileType Plugin for Paint.NET
//
// This software is provided under the MIT License:
//   Copyright (c) 2012-2017, 2023 Nicholas Hayes
//
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PaintDotNet;
using PaintDotNet.Imaging;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;

namespace OptimizedJPEG
{
    [PluginSupportInfo(typeof(PluginSupportInfo))]
    public sealed class OptimizedJPEGFiletype : PropertyBasedFileType
    {
        private enum CopyOptions
        {
            None,
            Comments,
            All
        }

        private enum PropertyNames
        {
            Quality,
            OptimizeEncoding,
            ProgressiveEncoding,
            CopyOptions,
            ChromaSubsampling
        }

        internal static string StaticName
        {
            get
            {
                return "Optimized JPEG";
            }
        }

        private static readonly string JpegtranPath = Path.Combine(Path.GetDirectoryName(typeof(OptimizedJPEGFiletype).Assembly.Location), "jpegtran.exe");
        private static readonly IReadOnlyList<string> FileExtensions = new string[] { ".jpg", ".jpeg", ".jpe", ".jfif" };

        private readonly string tempInput;
        private readonly string tempOutput;
        private readonly IServiceProvider services;

        public OptimizedJPEGFiletype(IServiceProvider services)
            : base(StaticName, new FileTypeOptions() { LoadExtensions = FileExtensions, SaveExtensions = FileExtensions })
        {
            tempInput = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            tempOutput = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            this.services = services;
        }


        public override PropertyCollection OnCreateSavePropertyCollection()
        {
            List<Property> props = new List<Property>{
                new Int32Property(PropertyNames.Quality, 95, 0, 100),
                CreateChromaSubsampling(),
                new BooleanProperty(PropertyNames.OptimizeEncoding, true),
                new BooleanProperty(PropertyNames.ProgressiveEncoding, false),
                StaticListChoiceProperty.CreateForEnum(PropertyNames.CopyOptions, CopyOptions.Comments, false)
            };

            return new PropertyCollection(props);

            static StaticListChoiceProperty CreateChromaSubsampling()
            {
                object[] valueChoices = new object[]
                {
                    JpegYCrCbSubsamplingOption.Subsample444,
                    JpegYCrCbSubsamplingOption.Subsample440,
                    JpegYCrCbSubsamplingOption.Subsample422,
                    JpegYCrCbSubsamplingOption.Subsample420,
                };

                return new StaticListChoiceProperty(PropertyNames.ChromaSubsampling, valueChoices, 1);
            }
        }

        public override ControlInfo OnCreateSaveConfigUI(PropertyCollection props)
        {
            ControlInfo info = CreateDefaultSaveConfigUI(props);

            PropertyControlInfo qualityPCI = info.FindControlForPropertyName(PropertyNames.Quality);
            qualityPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = "Quality";

            PropertyControlInfo chromaSubsamplingPCI = info.FindControlForPropertyName(PropertyNames.ChromaSubsampling);
            chromaSubsamplingPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = "Chroma Subsampling";
            chromaSubsamplingPCI.SetValueDisplayName(JpegYCrCbSubsamplingOption.Subsample444, "4:4:4 (Best Quality)");
            chromaSubsamplingPCI.SetValueDisplayName(JpegYCrCbSubsamplingOption.Subsample440, "4:4:0");
            chromaSubsamplingPCI.SetValueDisplayName(JpegYCrCbSubsamplingOption.Subsample422, "4:2:2");
            chromaSubsamplingPCI.SetValueDisplayName(JpegYCrCbSubsamplingOption.Subsample420, "4:2:0 (Best Compression)");

            PropertyControlInfo optimizeEncodingPCI = info.FindControlForPropertyName(PropertyNames.OptimizeEncoding);
            optimizeEncodingPCI.ControlProperties[ControlInfoPropertyNames.Description].Value = "Optimize Encoding";
            optimizeEncodingPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = string.Empty;

            PropertyControlInfo progressiveEncodingPCI = info.FindControlForPropertyName(PropertyNames.ProgressiveEncoding);
            progressiveEncodingPCI.ControlProperties[ControlInfoPropertyNames.Description].Value = "Progressive Encoding";
            progressiveEncodingPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = string.Empty;

            PropertyControlInfo copyOptionsPCI = info.FindControlForPropertyName(PropertyNames.CopyOptions);
            copyOptionsPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = "Metadata Copy Options";
            copyOptionsPCI.ControlType.Value = PropertyControlType.RadioButton;

            return info;
        }

        protected override Document OnLoad(Stream input)
        {
            IFileTypesService fileTypesService = services.GetService<IFileTypesService>() ?? throw new InvalidOperationException($"Failed to get the {nameof(IFileTypesService)}.");
            IFileTypeInfo jpegFileType = fileTypesService.GetJpegFileType();

            return jpegFileType.GetInstance().Load(input);
        }

        private static unsafe bool IsGrayscaleImage(Document input, Surface scratchSurface)
        {
            input.CreateRenderer().Render(scratchSurface);

            for (int y = 0; y < scratchSurface.Height; y++)
            {
                ColorBgra* ptr = scratchSurface.GetRowPointerUnchecked(y);
                ColorBgra* ptrEnd = ptr + scratchSurface.Width;

                while (ptr < ptrEnd)
                {
                    if (!(ptr->R == ptr->G && ptr->G == ptr->B))
                    {
                        return false;
                    }
                    ptr++;
                }
            }

            return true;
        }

        private string BuildArguments(PropertyBasedSaveConfigToken token, Document input, Surface scratchSurface)
        {
            bool optimize = token.GetProperty<BooleanProperty>(PropertyNames.OptimizeEncoding).Value;
            bool progressive = token.GetProperty<BooleanProperty>(PropertyNames.ProgressiveEncoding).Value;
            CopyOptions copy = (CopyOptions)token.GetProperty(PropertyNames.CopyOptions).Value;

            string copyOption = string.Empty;

            switch (copy)
            {
                case CopyOptions.None:
                    copyOption = "none";
                    break;
                case CopyOptions.Comments:
                    copyOption = "comments";
                    break;
                case CopyOptions.All:
                    copyOption = "all";
                    break;
            }

            return string.Format("-copy {0} {1} {2} {3} -outfile \"{4}\" \"{5}\"", new object[] { copyOption,
                    optimize ? "-optimize" : string.Empty,
                    progressive ? "-progressive" : string.Empty,
                    IsGrayscaleImage(input, scratchSurface) ? "-grayscale" : string.Empty,
                    tempOutput,
                    tempInput});
        }

        protected override void OnSaveT(Document input, Stream output, PropertyBasedSaveConfigToken token, Surface scratchSurface, ProgressEventHandler progressCallback)
        {
            int quality = token.GetProperty<Int32Property>(PropertyNames.Quality).Value;
            JpegYCrCbSubsamplingOption subsampling = (JpegYCrCbSubsamplingOption)token.GetProperty(PropertyNames.ChromaSubsampling).Value;

            IFileTypesService fileTypesService = services.GetService<IFileTypesService>() ?? throw new InvalidOperationException($"Failed to get the {nameof(IFileTypesService)}.");
            IJpegFileType jpegFileType = (IJpegFileType)fileTypesService.GetJpegFileType().GetInstance();
            IJpegFileTypeSaveToken jpegFileTypeSaveToken = jpegFileType.CreateSaveToken();

            jpegFileTypeSaveToken.Quality = quality;
            jpegFileTypeSaveToken.YCrCbSubsampling = subsampling;

            using (FileStream fs = new FileStream(tempInput, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                jpegFileType.Save(input, fs, jpegFileTypeSaveToken);
            }

            using (Process process = new Process())
            {
                ProcessStartInfo psi = new ProcessStartInfo(JpegtranPath, BuildArguments(token, input, scratchSurface))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.StartInfo = psi;

                process.Start();

                process.WaitForExit();
            }

            using (FileStream stream = new FileStream(tempOutput, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.CopyTo(output);
            }

            File.Delete(tempInput);
            File.Delete(tempOutput);
        }
    }
}
