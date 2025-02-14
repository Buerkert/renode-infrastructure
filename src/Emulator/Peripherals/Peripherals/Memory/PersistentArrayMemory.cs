//
// Copyright (c) 2025 Burkert
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.IO;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Memory
{
    public class PersistentArrayMemory : ArrayMemory, IDisposable
    {
        public PersistentArrayMemory(int size, string path = null, byte paddingVal = 0) : base(size)
        {
            this.paddingVal = paddingVal;
            if (path != null)
                LinkFile(path);
        }

        public void Dispose()
        {
            try
            {
                if (LinkedPath != null)
                    SaveInternal();
                else
                    this.Log(LogLevel.Info, "Memory is not linked to any file, skipping save");
            }
            catch (Exception e)
            {
                this.Log(LogLevel.Error, $"Failed to save memory to '{LinkedPath}': {e.Message}");
            }
        }

        public void LinkFile(string linkPath, bool create = true, bool sameSize = true)
        {
            try
            {
                var fileData = File.ReadAllBytes(linkPath);
                if (fileData.Length != array.Length)
                {
                    if (sameSize)
                        throw new RecoverableException($"Linked file '{linkPath}' has different size than memory.");
                    this.Log(LogLevel.Warning, $"Linked file '{linkPath}' is {fileData.Length} bytes long, while memory is {array.Length} bytes long.");
                }

                this.Log(LogLevel.Debug, $"Copying data from linked file '{linkPath}'");
                Array.Copy(fileData, array, Math.Min(fileData.Length, array.Length));
                if (fileData.Length < array.Length)
                {
                    this.Log(LogLevel.Debug, $"Filling remaining memory with 0x{paddingVal:X}");
                    Array.Fill(array, paddingVal, fileData.Length, array.Length - fileData.Length);
                }
            }
            catch (FileNotFoundException)
            {
                if (!create)
                    throw new RecoverableException($"Linked file '{linkPath}' does not exist.");
                this.Log(LogLevel.Info, $"Linked file '{linkPath}' does not exist, memory will filled with 0x{paddingVal:X}");
                Array.Fill(array, paddingVal);
            }
            catch (RecoverableException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RecoverableException($"Failed to read linked file: {e.Message}", e);
            }

            LinkedPath = linkPath;
            this.Log(LogLevel.Debug, $"Linked memory to file '{linkPath}'");
        }

        public void UnlinkFile() => LinkedPath = null;

        public void SaveToFile()
        {
            if (LinkedPath == null)
                throw new RecoverableException("Path is not set.");
            try
            {
                SaveInternal();
            }
            catch (Exception e)
            {
                throw new RecoverableException($"Failed to save memory to file: {e.Message}", e);
            }
        }

        public string LinkedPath { get; private set; }

        private void SaveInternal()
        {
            this.Log(LogLevel.Debug, $"Saving memory to file '{LinkedPath}'");
            File.WriteAllBytes(LinkedPath, array);
        }


        private readonly byte paddingVal;
    }
}