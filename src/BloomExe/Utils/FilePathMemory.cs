﻿using System;
using System.Collections.Generic;
using System.IO;

namespace Bloom.Utils
{
    public static class FilePathMemory
    {
        // map from the book ID (if relevant) and file type (extension + possible extra tag) to the most recent full file pathname
        static Dictionary<Tuple<string, string>, string> _savedFilePaths =
            new Dictionary<Tuple<string, string>, string>();

        // map from the file type or other tag to the most recently used folder for that file type
        static Dictionary<string, string> _savedFolderPaths = new Dictionary<string, string>();

        public static void RememberFilePath(Tuple<string, string> key, string filePath)
        {
            _savedFilePaths[key] = filePath;
        }

        public static void RememberFolderPath(string key, string folderPath)
        {
            _savedFolderPaths[key] = folderPath;
        }

        public static bool TryGetRememberedFilePath(Tuple<string, string> key, out string path)
        {
            return _savedFilePaths.TryGetValue(key, out path);
        }

        public static bool TryGetRememberedFolderPath(string key, out string path)
        {
            return _savedFolderPaths.TryGetValue(key, out path);
        }

        /// <summary>
        /// Get the output file path for the given book and output type provided by the extension (plus extraTag)
        /// The default is to use the folder name from the book for the filename (with the extension added) and
        /// the user's Documents folder for where to put the file.
        /// Call this before invoking a file open/save dialog to provide an initial path.
        /// </summary>
        /// <param name="book">The Book object</param>
        /// <param name="extension">output type extension (".pdf", ".epub", ".mp4", etc.</param>
        /// <param name="proposedName">A name proposed for the book's output file (optional).  Should not include the extension.
        /// Not used if a path is remembered.</param>
        /// <param name="extraTag">Additional information for variant of output such as language tag or PDF type of output (optional).
        /// Must match corresponding RememberOutputFilePath call.  If this parameter is used, then proposed name should also be used
        /// although it may be ignored after the first time.  (The proposed name may or may not include the extra tag information as
        /// part of the name.)</param>
        /// <param name="proposedFolder">A folder proposed for the book's output file (optional).  It must exist to be used.  Not used
        /// if a path is remembered.</param>
        /// <returns>full path of a proposed output file, possibly remembered from an earlier call to RememberOutputFilePath</returns>
        /// <remarks>
        /// This method does not store any information, but does look up any relevent stored data.
        /// </remarks>
        public static string GetOutputFilePath(
            Book.Book book,
            string extension,
            string proposedName = null,
            string extraTag = "",
            string proposedFolder = null
        )
        {
            if (
                TryGetRememberedFilePath(
                    GetCompoundTag(book.ID, extension, extraTag),
                    out string path
                )
            )
                return path;
            string startingFolder;
            if (!TryGetRememberedFolderPath(extension, out startingFolder))
            {
                if (!String.IsNullOrEmpty(proposedFolder) && Directory.Exists(proposedFolder))
                    startingFolder = proposedFolder;
                else
                    startingFolder = Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments
                    );
            }
            if (!String.IsNullOrEmpty(proposedName))
                return (Path.Combine(startingFolder, $"{proposedName}{extension}"));
            return Path.Combine(startingFolder, $"{Path.GetFileName(book.FolderPath)}{extension}");
        }

        /// <summary>
        /// Remember an output file path for the given book and output type provided by the extension (plus extraTag).
        /// Call this with the value returned from a file open/save dialog.
        /// </summary>
        /// <param name="book">The Book Object</param>
        /// <param name="extension">output type extension (".pdf", ".epub", ".mp4", etc.</param>
        /// <param name="outputFilePath">full path of the output file including directories and extension</param>
        /// <param name="extraTag">Additional information for variant of output such as language tag or PDF type of output (optional).  Must match corresponding GetOutputFilePath call.</param>
        /// <remarks>
        /// extraTag is used as part of the key for remembering the full file path, but NOT for remembering the folder.
        /// I'm not sure what the best approach is for granularity of remembering the folder, and opted for less granular.
        /// It doesn't even use the book's ID in the key, just the extension.
        /// </remarks>
        public static void RememberOutputFilePath(
            Book.Book book,
            string extension,
            string outputFilePath,
            string extraTag = ""
        )
        {
            RememberFilePath(GetCompoundTag(book.ID, extension, extraTag), outputFilePath);
            RememberFolderPath(extension, Path.GetDirectoryName(outputFilePath));
        }

        private static Tuple<string, string> GetCompoundTag(
            string id = "",
            string extension = "",
            string extraTag = ""
        )
        {
            return Tuple.Create(id, $"{extraTag}{extension}");
        }

        public static string GetOutputFilePath(
            Collection.BookCollection collection,
            string extension
        )
        {
            if (
                _savedFilePaths.TryGetValue(
                    GetCompoundTag(collection.PathToDirectory, extension, ""),
                    out string path
                )
            )
                return path;
            string startingFolder;
            if (!_savedFolderPaths.TryGetValue(extension, out startingFolder))
            {
                startingFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            return Path.Combine(startingFolder, $"{collection.Name}{extension}");
        }

        public static void RememberOutputFilePath(
            Collection.BookCollection collection,
            string extension,
            string outputFilePath
        )
        {
            _savedFilePaths[GetCompoundTag(collection.PathToDirectory, extension, "")] =
                outputFilePath;
            _savedFolderPaths[extension] = Path.GetDirectoryName(outputFilePath);
        }

        public static void ResetFilePathMemory()
        {
            if (!Program.RunningUnitTests)
                throw new ApplicationException(
                    "ResetOutputFilenamesMemory() can be called only by unit tests!"
                );
            _savedFilePaths.Clear();
            _savedFolderPaths.Clear();
        }
    }
}
