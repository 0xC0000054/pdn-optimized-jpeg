/////////////////////////////////////////////////////////////////////////////////
//
// Optimized JPEG FileType Plugin for Paint.NET
// 
// This software is provided under the MIT License:
//   Copyright (c) 2012-2017 Nicholas Hayes
//
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

// Portions of this file has been adapted from:
/////////////////////////////////////////////////////////////////////////////////
// Paint.NET                                                                   //
// Copyright (C) dotPDN LLC, Rick Brewster, Tom Jackson, and contributors.     //
// Portions Copyright (C) Microsoft Corporation. All Rights Reserved.          //
// See src/Resources/Files/License.txt for full licensing and attribution      //
// details.                                                                    //
// .                                                                           //
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PaintDotNet;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;

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
            CopyOptions
        }

        internal static string StaticName
        {
            get
            {
                return "Optimized JPEG";
            }
        }

        private static readonly string JpegtranPath = Path.Combine(Path.GetDirectoryName(typeof(OptimizedJPEGFiletype).Assembly.Location), "jpegtran.exe");

        private readonly string tempInput;
        private readonly string tempOutput;

        public OptimizedJPEGFiletype()
            : base(StaticName, FileTypeFlags.SupportsLoading | FileTypeFlags.SupportsSaving, new string[] { ".jpg", ".jpeg", ".jpe", ".jfif" })
        {
            this.tempInput = Path.Combine(Path.GetTempPath(), "inputTemp.jpg");
            this.tempOutput = Path.Combine(Path.GetTempPath(), "optimizedTemp.jpg");
        }


        public override PropertyCollection OnCreateSavePropertyCollection()
        {
            List<Property> props = new List<Property>{
                new Int32Property(PropertyNames.Quality, 95, 0, 100),
                new BooleanProperty(PropertyNames.OptimizeEncoding, true),
                new BooleanProperty(PropertyNames.ProgressiveEncoding, false),
                StaticListChoiceProperty.CreateForEnum(PropertyNames.CopyOptions, CopyOptions.Comments, false)
            };

            return new PropertyCollection(props);
        }

        public override ControlInfo OnCreateSaveConfigUI(PropertyCollection props)
        {
            ControlInfo info = CreateDefaultSaveConfigUI(props);
            info.SetPropertyControlValue(PropertyNames.Quality, ControlInfoPropertyNames.DisplayName, "Quality");

            info.SetPropertyControlValue(PropertyNames.OptimizeEncoding, ControlInfoPropertyNames.Description, "Optimize Encoding");
            info.SetPropertyControlValue(PropertyNames.OptimizeEncoding, ControlInfoPropertyNames.DisplayName, string.Empty);

            info.SetPropertyControlValue(PropertyNames.ProgressiveEncoding, ControlInfoPropertyNames.Description, "Progressive Encoding");
            info.SetPropertyControlValue(PropertyNames.ProgressiveEncoding, ControlInfoPropertyNames.DisplayName, string.Empty);

            info.SetPropertyControlValue(PropertyNames.CopyOptions, ControlInfoPropertyNames.DisplayName, "Metadata Copy Options");
            info.SetPropertyControlType(PropertyNames.CopyOptions, PropertyControlType.RadioButton);

            return info;
        }

        protected override Document OnLoad(Stream input)
        {
            using (Image image = Image.FromStream(input))
            {
                return Document.FromGdipImage(image, false);
            }
        }

        private static void LoadProperties(Image dstImage, Document srcDoc)
        {
            Bitmap asBitmap = dstImage as Bitmap;

            if (asBitmap != null)
            {
                // Sometimes GDI+ does not honor the resolution tags that we
                // put in manually via the EXIF properties.
                float dpiX;
                float dpiY;

                switch (srcDoc.DpuUnit)
                {
                    case MeasurementUnit.Centimeter:
                        dpiX = (float)Document.DotsPerCmToDotsPerInch(srcDoc.DpuX);
                        dpiY = (float)Document.DotsPerCmToDotsPerInch(srcDoc.DpuY);
                        break;

                    case MeasurementUnit.Inch:
                        dpiX = (float)srcDoc.DpuX;
                        dpiY = (float)srcDoc.DpuY;
                        break;

                    default:
                    case MeasurementUnit.Pixel:
                        dpiX = 1.0f;
                        dpiY = 1.0f;
                        break;
                }

                try
                {
                    asBitmap.SetResolution(dpiX, dpiY);
                }
                catch (Exception)
                {
                    // Ignore error
                }
            }

            Metadata metaData = srcDoc.Metadata;

            foreach (string key in metaData.GetKeys(Metadata.ExifSectionName))
            {
                string blob = metaData.GetValue(Metadata.ExifSectionName, key);
                PropertyItem pi = PaintDotNet.SystemLayer.PropertyItem2.FromBlob(blob).ToPropertyItem();

                try
                {
                    dstImage.SetPropertyItem(pi);
                }
                catch (ArgumentException)
                {
                    // Ignore error: the image does not support property items
                }
            }
        }

        private static ImageCodecInfo GetImageCodecInfo(ImageFormat format)
        {
            ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();

            foreach (ImageCodecInfo icf in encoders)
            {
                if (icf.FormatID == format.Guid)
                {
                    return icf;
                }
            }

            return null;
        }

        private static unsafe bool IsGrayscaleImage(Surface scratchSurface)
        {
            for (int y = 0; y < scratchSurface.Height; y++)
            {
                ColorBgra* ptr = scratchSurface.GetRowAddressUnchecked(y);
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

        private string BuildArguments(PropertyBasedSaveConfigToken token, Surface scratchSurface)
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

            return string.Format("-copy {0} {1} {2} {3} {4} {5}", new object[] { copyOption,
                    optimize ? "-optimize" : string.Empty,
                    progressive ? "-progressive" : string.Empty,
                    IsGrayscaleImage(scratchSurface) ? "-grayscale" : string.Empty,
                    tempInput,
                    tempOutput});
        }

        protected override void OnSaveT(Document input, Stream output, PropertyBasedSaveConfigToken token, Surface scratchSurface, ProgressEventHandler progressCallback)
        {
            int quality = token.GetProperty<Int32Property>(PropertyNames.Quality).Value;

            ImageCodecInfo icf = GetImageCodecInfo(ImageFormat.Jpeg);
            EncoderParameters encoderOptions = new EncoderParameters(1);
            encoderOptions.Param[0] = new EncoderParameter(Encoder.Quality, quality);

            scratchSurface.Clear(ColorBgra.White);
            using (RenderArgs ra = new RenderArgs(scratchSurface))
            {
                input.Render(ra, false);
            }

            using (FileStream fs = new FileStream(tempInput, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                using (Bitmap bitmap = scratchSurface.CreateAliasedBitmap())
                {
                    LoadProperties(bitmap, input);
                    bitmap.Save(fs, icf, encoderOptions);
                }
            }

            using (Process process = new Process())
            {
                ProcessStartInfo psi = new ProcessStartInfo(JpegtranPath, BuildArguments(token, scratchSurface))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.StartInfo = psi;

                process.Start();

                process.WaitForExit();
            }

            const int BufferSize = 4096;

            using (FileStream stream = new FileStream(tempOutput, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead = 0;

                while ((bytesRead = stream.Read(buffer, 0, BufferSize)) > 0)
                {
                    output.Write(buffer, 0, bytesRead);
                } 
            }

            File.Delete(tempInput);
            File.Delete(tempOutput);
        }
    }
}
