using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Services;

/// <summary>need06-03：提示词渲染上下文（变量替换输入）。</summary>
public sealed record PromptContext(
    string OsVersion,
    string OsArch,
    string WsName,
    string WsRoot,
    string PythonStatus,
    string Today
);
