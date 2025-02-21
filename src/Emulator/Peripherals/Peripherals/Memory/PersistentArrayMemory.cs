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
    /// <summary>
    /// Represents a memory that can be linked to a file. The memory can either be saved manually or automatically when disposed.
    /// </summary>
    public class PersistentArrayMemory : ArrayMemory, IDisposable
    {
        /// <summary>
        /// Creates a new instance of the memory and try to link to file if path is provided.
        /// </summary>
        /// <param name="size">Size of the memory in bytes.</param>
        /// <param name="path">Path to the file to link to.</param>
        /// <param name="create">If true, the file doesnt have to exist and the memory will be filled with <paramref name="paddingVal"/></param>
        /// <param name="sameSize">If true, the file must have the same size as the memory, otherwise the file content will be truncated or padded with <paramref name="paddingVal"/></param>
        /// <param name="paddingVal">Value to fill the memory with if the file is too small or does not exist.</param>
        /// <param name="saveOnReset">If true, the memory content will be saved to the file on reset.</param>
        public PersistentArrayMemory(int size, string path = null, bool create = true, bool sameSize = true, byte paddingVal = 0,
            bool saveOnReset = false) : base(size)
        {
            SaveOnReset = saveOnReset;
            this.paddingVal = paddingVal;
            try
            {
                if (!string.IsNullOrEmpty(path))
                    LinkFile(path, create, sameSize);
            }
            catch (RecoverableException e)
            {
                throw new ConstructionException($"Failed to link file: {e.Message}", e);
            }
        }

        public override void Reset()
        {
            if (SaveOnReset)
            {
                if (!string.IsNullOrEmpty(LinkedPath))
                    SaveInternal();
                else
                    this.Log(LogLevel.Warning, "Memory is not linked to any file, skipping save on reset");
            }

            base.Reset();
        }

        public void Dispose()
        {
            try
            {
                if (!string.IsNullOrEmpty(LinkedPath))
                    SaveInternal();
                else
                    this.Log(LogLevel.Info, "Memory is not linked to any file, skipping save");
            }
            catch (Exception e)
            {
                this.Log(LogLevel.Error, $"Failed to save memory to '{LinkedPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Links the memory to a file and copies the file content to the memory.
        /// If previous file was it will be overwritten without saving.
        /// </summary>
        /// <param name="linkPath">Path to the file to link to.</param>
        /// <param name="create">If true, the file doesnt have to exist and the memory will be filled with <see cref="paddingVal"/></param>
        /// <param name="sameSize">If true, the file must have the same size as the memory, otherwise the file content will be truncated or padded with <see cref="paddingVal"/></param>
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

        /// <summary>
        /// Unlinks the memory from the file.
        /// The memory content is not saved to the file.
        /// </summary>
        public void UnlinkFile()
        {
            this.Log(LogLevel.Debug, $"Unlinking memory from file '{LinkedPath}'");
            LinkedPath = null;
        }

        /// <summary>
        /// Saves the memory content to the linked file.
        /// If the memory is not linked to any file, an exception is thrown.
        /// </summary>
        public void SaveToFile()
        {
            if (!string.IsNullOrEmpty(LinkedPath))
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
        public bool SaveOnReset { get; set; }

        private void SaveInternal()
        {
            this.Log(LogLevel.Debug, $"Saving memory to file '{LinkedPath}'");
            File.WriteAllBytes(LinkedPath, array);
        }


        private readonly byte paddingVal;
    }
}