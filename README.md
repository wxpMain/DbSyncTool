# DbSyncTool — MES 中间库同步工具
> 一款面向制造业的 ERP → MES 数据同步桌面工具，支持定时调度、BOM分组、主从结构接口、查重去重、API回调回写等能力。

---

## 功能特性

- **多数据源支持** — SQL Server（鼎捷易助、申菲T+）/ MySQL，可同时配置多个连接
- **灵活的请求体模板** — `{{字段名}}` 占位符动态替换，支持普通单层和主从结构（`[[details]]`）
- **BOM分组同步** — 流式分页读取，流式分组聚合，Channel 有界并发，支持百万级数据不OOM
- **查重去重** — 基于 `synced_records` 标记表，分批查重（每批500），已同步数据自动跳过
- **接口成功回写** — API成功后自动执行 UPDATE 回写源库，失败不执行
- **占位符校验** — 替换后残留 `{{` 自动跳过，防止错误数据写入MES
- **业务层状态判断** — 解析响应体 `success` 字段，区分业务失败/跳过/成功
- **Cron 定时调度** — 基于 Quartz.NET，5位标准Cron自动转换，快捷预设按钮
- **系统托盘** — 关闭窗口后台运行，托盘双击恢复，定时任务不中断
- **开机自启** — 一键写入 Windows 注册表，开机自动后台运行
- **分接口日志** — 每个 API 接口单独日志文件，完整记录请求体/响应体/耗时

---

## 界面预览

```
┌─────────────────────────────────────────────────────────┐
│  DbSyncTool                                    _ □ ×   │
├──────────┬──────────────────────────────────────────────┤
│          │                                              │
│ 数据库配置│              主内容区域                      │
│          │                                              │
│ 存储过程  │                                              │
│          │                                              │
│ API调用  │                                              │
│          │                                              │
│ 任务配置  │                                              │
│          │                                              │
│ 定时同步  │                                              │
│          │                                              │
│ 日志查看  │                                              │
│          │                                              │
├──────────┴──────────────────────────────────────────────┤
│ ☐ 开机自启    状态栏信息                        v1.0.0  │
└─────────────────────────────────────────────────────────┘
```

---

## 快速开始

### 环境要求

- Windows 10/11 x64
- .NET 8 运行时（单文件发布版本无需额外安装）

### 下载运行

从 [Releases](../../releases) 页面下载最新版 `DbSyncTool.exe`，双击运行即可，无需安装。

### 首次配置流程

```
1. 数据库配置  →  添加 ERP 数据库（SQL Server）连接
                  如需查重，再添加 MySQL 数据库连接

2. API调用    →  添加 MES 接口配置（地址、登录信息、Token路径）

3. 存储过程   →  新增配置：
                  - 填写 SELECT 查询SQL
                  - 验证SQL → 解析列名 → 生成字段映射
                  - 输出方式选「调用API写入」
                  - 填写请求体模板（{{字段名}} 占位）
                  - 可选：开启查重、配置回写SQL

4. 任务配置   →  新增任务：
                  - 关联存储过程配置
                  - 设置触发方式（Cron/循环/一次性）

5. 定时同步   →  启动调度器 → 点击「立即执行」验证 → 查看日志
```

---

## 请求体模板说明

### 普通单层接口

```json
{
    "code": "{{code}}",
    "title": "{{title}}",
    "unitCode": "{{unitCode}}"
}
```

### 含明细列表接口（主从结构）

主模板：
```json
[{
    "code": "{{code}}",
    "outboundDate": "{{outboundDate}}",
    "details": [[details]]
}]
```

子数组模板：
```json
{
    "productCode": "{{productCode}}",
    "expectedQty": {{expectedQty}}
}
```

> `[[details]]` 会被工具自动替换为按分组字段聚合的子数组。

### 占位符规则

| 占位符 | 说明 |
|------|------|
| `{{字段名}}` | SQL查询列别名对应的值，字符串加引号，数字不加 |
| `[[details]]` | 子数组展开位置，仅用于主模板 |
| `{{now}}` | 回写SQL专用，替换为工具运行时的当前时间 |

---

## 查重配置

在 MySQL 中建表（首次使用执行一次）：

```sql
CREATE TABLE IF NOT EXISTS synced_records (
    orderCode varchar(100) NOT NULL,
    syncType  varchar(50)  NOT NULL,
    syncTime  datetime     DEFAULT NOW(),
    PRIMARY KEY (orderCode, syncType)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

存储过程配置中：
- 勾选「启用查重」
- 查重表：`synced_records`
- 唯一键：填写分组字段名（如 `orderCode`）

---

## Cron 表达式

工具使用标准5位 Cron（分 时 日 月 周），自动转换为 Quartz.NET 格式。

| 快捷按钮 | 表达式 | 说明 |
|------|------|------|
| 每天0点 | `0 0 * * *` | 每天凌晨0点 |
| 每天8点 | `0 8 * * *` | 每天早上8点 |
| 每小时 | `0 * * * *` | 每整点执行 |
| 每30分钟 | `*/30 * * * *` | 每30分钟执行 |

也可手动填写6位 Quartz 格式，如 `0 0 21 1/1 * ?`（每天21点）。

---

## 日志说明

日志存放目录：`E:\MESCenterLog\`

| 文件名格式 | 内容 |
|------|------|
| `中间库运行日志-yyyy-MM-dd-001.log` | INFO 及以上级别运行日志 |
| `中间库日志-yyyy-MM-dd-001.log` | ERROR 及以上级别错误日志 |
| `{接口名称}-yyyy-MM-dd-001.log` | 每个API接口的详细调用日志 |

API 日志示例：

```
[2026-06-03 14:31:51.731] [SUCCESS]
  API名称  : 物料清单批量调入
  请求方向 : POST http://192.168.10.252:8280/admin/productInfo/batchImport
  请求体   : [BOM:268A02 1条] [{"code":"268A02","title":"A02",...}]
  状态码   : 200
  响应体   : {"status":200,"msg":"操作成功","success":true}
  耗时     : 194ms
```

---

## 技术栈

| 组件 | 版本 | 用途 |
|------|------|------|
| .NET | 8.0 | 运行框架 |
| WPF | - | 桌面UI框架 |
| Quartz.NET | 3.8.1 | 定时任务调度 |
| Newtonsoft.Json | 13.0.3 | JSON序列化/解析 |
| Microsoft.Data.SqlClient | 5.2.1 | SQL Server 连接 |
| MySqlConnector | 2.3.5 | MySQL 连接 |
| Polly | 8.3.1 | 重试策略 |
| Serilog | 3.1.1 | 日志框架 |

---

## 打包发布

在项目根目录执行：

```powershell
dotnet publish DbSyncTool/DbSyncTool.csproj -c Release -r win-x64 --self-contained true "-p:PublishSingleFile=true" -o "发布目录路径"
```

发布后将 `Config\` 目录一并复制到发布目录，可直接迁移已有配置。

---

## License

MIT License © 2026
