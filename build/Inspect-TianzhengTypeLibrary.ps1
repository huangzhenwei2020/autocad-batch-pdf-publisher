[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LibraryPath,
    [string]$NamePattern = 'Balcony|Rail|Wall|Slab|Roof|Water|Apron|Flash',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$library = (Resolve-Path -LiteralPath $LibraryPath).Path
# REGKIND_NONE reads metadata without registering COM classes or calling getters.
if (-not ('WanLuo.Diagnostics.TypeLibraryReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
namespace WanLuo.Diagnostics
{
    public sealed class TypeMember
    {
        public string Name { get; set; }
        public string Invocation { get; set; }
        public int Parameters { get; set; }
        public string Description { get; set; }
    }
    public sealed class TypeDescription
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Guid { get; set; }
        public List<TypeMember> Members { get; set; } = new List<TypeMember>();
    }
    public static class TypeLibraryReader
    {
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void LoadTypeLibEx(string path, int kind, out ITypeLib library);
        public static List<TypeDescription> Read(string path, string pattern)
        {
            ITypeLib library;
            LoadTypeLibEx(path, 2, out library);
            var result = new List<TypeDescription>();
            try
            {
                for (int i = 0; i < library.GetTypeInfoCount(); i++)
                {
                    string name, description, help; int context;
                    library.GetDocumentation(i, out name, out description, out context, out help);
                    if (!Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)) continue;
                    ITypeInfo info; library.GetTypeInfo(i, out info);
                    IntPtr attribute = IntPtr.Zero;
                    try
                    {
                        info.GetTypeAttr(out attribute);
                        var attr = Marshal.PtrToStructure<TYPEATTR>(attribute);
                        var item = new TypeDescription { Name = name, Kind = attr.typekind.ToString(), Guid = attr.guid.ToString() };
                        result.Add(item);
                        for (int j = 0; j < attr.cFuncs; j++)
                        {
                            IntPtr pointer = IntPtr.Zero;
                            try
                            {
                                info.GetFuncDesc(j, out pointer);
                                var function = Marshal.PtrToStructure<FUNCDESC>(pointer);
                                info.GetDocumentation(function.memid, out name, out description, out context, out help);
                                item.Members.Add(new TypeMember { Name = name, Description = description,
                                    Invocation = function.invkind.ToString(), Parameters = function.cParams });
                            }
                            finally { if (pointer != IntPtr.Zero) info.ReleaseFuncDesc(pointer); }
                        }
                    }
                    finally
                    {
                        if (attribute != IntPtr.Zero) info.ReleaseTypeAttr(attribute);
                        Marshal.ReleaseComObject(info);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(library); }
            return result;
        }
    }
}
'@
}
$types = [WanLuo.Diagnostics.TypeLibraryReader]::Read($library, $NamePattern)
$report = [ordered]@{
    Purpose = 'Metadata only; members are NOT verified safe getters or model mappings.'
    Library = $library
    SHA256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash
    Types = @($types)
}
$json = $report | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $absolute = [IO.Path]::GetFullPath($OutputPath)
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $absolute))) { throw 'Output parent must exist.' }
    if (Test-Path -LiteralPath $absolute) { throw 'Output already exists; select a new report path.' }
    $json | Set-Content -LiteralPath $absolute -Encoding UTF8
}
$json
