# Stage 05 - 03：标准文件与检索基础工具（`read`/`write`/`edit`/`list_dir`/`grep`）实现方案

> **所属阶段**：Stage 05
> **核心对应需求**：需求 #6（在上下文中提供几个标准基础的 tool，例如文本 `edit`/`read`/`write` 等基础但标准的 tool 给 LLM 进行调用）

---

## 一、 `read_file`：带行号分页与自动编码识别的文件读取工具

### 1. JSON Schema
```json
{
  "name": "read_file",
  "description": "Read a text file from the workspace with 1-based line numbers. Supports pagination via offset and limit for large files.",
  "parameters": {
    "type": "object",
    "properties": {
      "file_path": {
        "type": "string",
        "description": "Relative path from workspace root or absolute file path."
      },
      "offset": {
        "type": "integer",
        "description": "1-based starting line number. Defaults to 1."
      },
      "limit": {
        "type": "integer",
        "description": "Maximum number of lines to read. Defaults to 500, max 2000."
      }
    },
    "required": ["file_path"]
  }
}
```

### 2. 核心实现细节
* **二进制文件防呆检测**：读取前检查文件前 `4096` 字节是否包含 `\0` (Null Byte)；若为图片/可执行文件等二进制文件，拒绝按文本展开并返回文件大小与类型提示。
* **BOM + UTF-8 + GBK 三级编码探测**：
  1. 先检测 BOM 头（UTF-8 BOM / UTF-16 LE / UTF-16 BE）；
  2. 无 BOM 时用 `StrictUtf8` 尝试解码；
  3. 若含非 UTF-8 字节，自动使用 `CP936 (GBK)` 解码（Win7 老 C/C++/C#/批处理项目大量源码为 GB2312/GBK 编码！），并在返回头部注明 `[Encoding: GBK, LineEndings: CRLF, TotalLines: 320]`。
* **输出格式**：每行带行号前缀 `   1: using System;`，方便后续 `edit_file` 精确定位。

---

## 二、 `write_file`：保持编码与原子落盘的文件写入工具

### 1. JSON Schema
```json
{
  "name": "write_file",
  "description": "Create a new text file or completely overwrite an existing file.",
  "parameters": {
    "type": "object",
    "properties": {
      "file_path": {
        "type": "string",
        "description": "Relative path from workspace root or absolute file path."
      },
      "content": {
        "type": "string",
        "description": "Full text content to write to the file."
      }
    },
    "required": ["file_path", "content"]
  }
}
```

### 2. 核心实现细节
* **自动创建父目录**：若目标文件所在的子目录不存在，自动 `Directory.CreateDirectory`。
* **编码与换行符继承**：
  * 若覆盖已存在的文件：先探测原文件的编码（如原文件是 GBK 或带 BOM 的 UTF-8，及原文件使用的是 `\r\n` 还是 `\n`），写入时默认保持原文件的编码与换行符风格，避免造成整个文件在 Git 中全行标红！
  * 若新建文件：默认采用无 BOM 的 `UTF-8` 编码。
* **原子写入**：先写入同目录临时文件再 `File.Move(..., overwrite: true)`。

---

## 三、 `edit_file`：CRLF/LF 容错精确替换与 Diff 生成工具

### 1. JSON Schema
```json
{
  "name": "edit_file",
  "description": "Perform an exact string replacement in an existing text file. Prefer this over write_file when modifying existing files.",
  "parameters": {
    "type": "object",
    "properties": {
      "file_path": {
        "type": "string",
        "description": "Target file path."
      },
      "old_string": {
        "type": "string",
        "description": "Exact existing text block to replace (include 2-5 lines of surrounding context to ensure uniqueness)."
      },
      "new_string": {
        "type": "string",
        "description": "Replacement text block."
      },
      "replace_all": {
        "type": "boolean",
        "description": "If true, replace all occurrences. If false (default), old_string must match exactly once."
      }
    },
    "required": ["file_path", "old_string", "new_string"]
  }
}
```

### 2. 核心实现细节（解决 Windows 下 LLM 换行符不匹配痛点）
LLM 生成的 JSON 字符串中换行符永远是 `\n` (LF)，而 Windows 本地代码文件通常是 `\r\n` (CRLF)。若直接用 `fileContent.IndexOf(oldString)` 会因为 `\r\n` vs `\n` 导致 100% 匹配失败！
* **智能换行归一化匹配算法**：
  1. 读取原文件并记录其主换行符风格 `originalNewLine = fileContent.Contains("\r\n") ? "\r\n" : "\n"`。
  2. 将 `fileContent`、`old_string`、`new_string` 统一在内存中归一化为 `\n` 进行精确匹配计数。
  3. 唯一性校验：
     * 若匹配次数 `== 0`：返回明确错误提示 `"Error: old_string not found in file. Please use read_file to check the exact current content."`
     * 若匹配次数 `> 1` 且 `replace_all == false`：返回 `"Error: old_string matched N times. Please include more surrounding context lines or set replace_all: true."`
  4. 执行替换后，将结果还原为 `originalNewLine` 并按原文件编码原子写回磁盘。
  5. 生成上下文前后 3 行的 **Unified Diff 片段**，同时返回给 LLM 确认并推送给前端 UI 展示红绿代码对比卡片。

---

## 四、 `list_dir` 与 `grep_search`：工作区探索与检索工具

### 1. `list_dir`（目录结构浏览）
* 入参：`path`（默认 `"."`）、`recursive`（布尔值，默认 `false`）、`max_depth`（默认 `2`）。
* 自动排除黑名单目录：`.git`、`node_modules`、`bin`、`obj`、`dist`、`__pycache__`、`.vs`。
* 返回格式化的目录树及文件字节大小。

### 2. `grep_search`（纯 C# 跨代正则内容搜索）
* 入参：`pattern`（正则表达式）、`path`（可选子目录或文件）、`include_glob`（如 `"*.cs"`、`"*.py"`）。
* 使用 C# `Regex` + `Parallel.ForEachAsync` 在工作区文本文件中快速检索，返回最多前 `100` 处匹配的 `相对路径:行号: 匹配内容`，在 32 位和 64 位 Win7 上无需任何外部二进制依赖即可达到毫秒级搜索体验。
