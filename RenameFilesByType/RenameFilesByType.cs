using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EnvDTE80;
using RenameFilesByType.Helpers;

namespace RenameFilesByType
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class RenameFilesByType
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("d85c5e87-3213-4f74-9733-142524fb1b71");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        public static DTE2 _dte;

        /// <summary>
        /// Initializes a new instance of the <see cref="RenameFilesByType"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private RenameFilesByType(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static RenameFilesByType Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in RenameFilesByType's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            _dte = await package.GetServiceAsync(typeof(DTE)) as DTE2;

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RenameFilesByType(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            object item = ProjectHelpers.GetSelectedItem();
            string[] allFiles = FindAllFilesInSelectedPath(item);
            RenameAllFilesByType(allFiles);
        }

        private static void RenameAllFilesByType(string[] allFiles)
        {
            if (allFiles.Length == 0)
                return;

            foreach (string file in allFiles)
            {
                RenameFileByType(file);
            }
        }

        private static void RenameFileByType(string file)
        {
            var fileCode = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(fileCode);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            List<BaseTypeDeclarationSyntax> typesInSourceCode = GetAllTypesInSourceCode(root);

            if (!typesInSourceCode.Any())
                return;

            if (typesInSourceCode.Count() > 1)
                return;

            System.IO.FileInfo fi = new System.IO.FileInfo(file);
            if (fi.Exists)
            {
                var sourceType = typesInSourceCode.Single();
                try
                {
                    var typeName = GetTypeNameFromSourceType(sourceType);
                    var fileNameParts = fi.Name.Split('.');

                    if(typeName == fileNameParts[0])
                    {
                        return;
                    }

                    var hasPartialIdentifier = sourceType.Modifiers.Any(m => m.Text == "partial");
                    try
                    {
                        if (hasPartialIdentifier)
                        {
                            var fileNameSuffix = fileNameParts.Length > 2 ? "." + String.Join("", fileNameParts, 1, fileNameParts.Length - 2) : String.Empty;
                            fi.MoveTo(Path.Combine(fi.DirectoryName, typeName + fileNameSuffix + fi.Extension));
                        }
                        else
                        {
                            fi.MoveTo(Path.Combine(fi.DirectoryName, typeName + fi.Extension));
                        }
                    }
                    catch(Exception)
                    {
                        fi.MoveTo(Path.Combine(fi.DirectoryName, typeName+ "."+ Guid.NewGuid().ToString().Substring(0,5) + fi.Extension));
                    }
                }
                catch (InvalidDataException)
                {

                }
            }
        }

        private static List<BaseTypeDeclarationSyntax> GetAllTypesInSourceCode(CompilationUnitSyntax root)
        {
            List<BaseTypeDeclarationSyntax> types = new List<BaseTypeDeclarationSyntax>();

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            var enums = root.DescendantNodes().OfType<EnumDeclarationSyntax>();
            var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
            var structs = root.DescendantNodes().OfType<StructDeclarationSyntax>();
            types.AddRange(classes);
            types.AddRange(enums);
            types.AddRange(interfaces);
            types.AddRange(structs);

            return types;
        }

        private static string GetTypeNameFromSourceType(object obj)
        {
            switch (obj)
            {
                case ClassDeclarationSyntax _:
                    return ((ClassDeclarationSyntax)obj).Identifier.Text;
                case EnumDeclarationSyntax _:
                    return ((EnumDeclarationSyntax)obj).Identifier.Text;
                case InterfaceDeclarationSyntax _:
                    return ((InterfaceDeclarationSyntax)obj).Identifier.Text;
                case StructDeclarationSyntax _:
                    return ((StructDeclarationSyntax)obj).Identifier.Text;
                default:
                    throw new InvalidDataException();
            }
        }

        private static string[] FindAllFilesInSelectedPath(object item)
        {
            if (item == null)
                return null;

            var projectItem = item as ProjectItem;

            if (projectItem == null)
                return new string[] { };

            string fileName = projectItem.FileNames[1];
            if (File.Exists(fileName))
            {
                return new string[] { fileName };
            }
            else
            {
                return Directory.GetFiles(fileName, "*", SearchOption.AllDirectories);
            }
        }

    }
}
