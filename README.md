# SaveForge

本地存档编辑器。所有处理都在本机完成，文件不上传任何服务器。

支持 12 种存档格式的浏览与修改，覆盖 Unity、Unreal Engine、RPG Maker、Ren'Py 等主流引擎，并能自动识别和解包常见压缩层。

## 功能

- **自动识别格式**：按内容探测（魔数 + 结构验证），不依赖扩展名
- **树状字段浏览**：任意嵌套层级，选中即可编辑，改过的字段带标记
- **安全写回**：保存前自动生成 `.bak`；按原类型回写，不会把整数写成字符串
- **存档对比**：两份存档逐字段比较，标出新增 / 删除 / 修改
- **批量修改**：按字段名通配符统一改值（`*` 匹配任意、`?` 匹配单字符），可限定在选中子树内
- **物品名映射**：自动加载游戏的 `data/Items.json`，把库存 ID 显示成真实名称
- **导出 / 导入 JSON**：任意格式转成 JSON 查看，或导入外部编辑的值
- **字段搜索**：按名称、别名或值过滤树

## 支持的格式

| 格式 | 扩展名 | 读 | 写 |
|------|--------|----|----|
| JSON | json / dat / save / sav | ✅ | ✅ |
| XML | xml | ✅ | ✅ |
| INI / CFG | ini / cfg / conf / properties | ✅ | ✅ |
| NBT | dat / nbt | ✅ | ✅ |
| plist（XML） | plist | ✅ | ✅ |
| plist（binary） | plist | ✅ | ✅ |
| Unreal Engine | sav / gvas | ✅ | ✅ |
| Ren'Py | save | ✅ | ✅ |
| RPG Maker XP/VX/VX Ace | rxdata / rvdata / rvdata2 | ✅ | ✅ |
| RPG Maker MV/MZ | rpgsave / rmmzsave | ✅ | ✅ |
| MessagePack | msgpack / mpack | ✅ | ✅ |
| CBOR | cbor | ✅ | ✅ |
| 未知格式 | 任意 | ✅ 十六进制 | ❌ |

**自动解包的压缩层**：GZip、Zlib、Zstd、LZ4、BZip2、Brotli、Base64，以及 RPG Maker MZ 的 pako + UTF-8 包装格式。

## 快速开始

### 直接运行

从 [Releases](../../releases) 下载 `SaveForge.exe`，双击运行，无需安装 .NET 运行时。

三种打开方式：

1. 点「打开存档」
2. 把文件拖进窗口
3. 命令行 `SaveForge.exe "存档路径"`

### 从源码构建

需要 .NET 10 SDK。

```bash
dotnet build SaveForge.slnx
dotnet run --project src/SaveForge.App
```

### 发布单文件版本

```bash
dotnet publish src/SaveForge.App/SaveForge.App.csproj `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

产物为 `publish/SaveForge.exe`（约 60 MB，内含运行时）。

## 使用说明

1. 打开存档
2. 左侧树展开到目标字段，点击选中
3. 右侧「值」框修改，改过的字段出现红点
4. 点「保存修改」写入磁盘（自动生成 `.bak`）
5. 进游戏验证；若异常，把 `.bak` 改名覆盖即可回滚

应用内点右上角「关于」可查看完整说明、支持的格式与快捷键。

### RPG Maker 存档的物品名映射

RPG Maker 的库存以 `{物品ID: 数量}` 存储，单看 ID 没有意义。工具会自动做两步：

1. 从存档所在目录向上查找游戏的 `data/` 或 `www/data/`
2. 读取 `Items.json` / `Weapons.json` / `Armors.json`，把 ID 映射成名称

效果：

```
修改前:  51  = 99
修改后:  51 · 樱花鳟 = 99
```

找不到游戏目录时，点工具栏「物品库」手动选择 `Items.json` 即可。

映射只影响显示，保存时仍写回原始 ID，不会破坏存档。

### 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl + O` | 打开存档 |
| `Ctrl + S` | 保存修改 |
| `Ctrl + F` | 聚焦搜索框 |
| `Esc` | 清空搜索 |
| `F5` | 重新加载当前文件 |

### 保存后游戏读不了？

从 `.bak` 恢复：把 `xxx.bak` 重命名回原文件名即可。

常见原因：该游戏的存档带完整性校验（校验和、HMAC），需要针对性适配。

### 打开后是十六进制视图？

说明格式未识别。可能原因：加密存档、自定义二进制序列化，或该引擎的变体未被支持。

## 项目结构

```
src/
├── SaveForge.Core/          # 格式引擎（无 UI 依赖）
│   ├── Formats/             # 各格式的读写适配器
│   ├── Compression/         # 压缩层
│   ├── Models/              # SaveNode / SaveDocument
│   ├── IO/                  # 大小端读写器
│   ├── SaveDiff.cs          # 存档对比
│   ├── SaveBatchEdit.cs     # 批量修改
│   ├── SaveDocumentJson.cs  # JSON 导出 / 导入
│   └── SaveFileService.cs   # 统一入口
├── SaveForge.App/           # WPF 界面
└── ThirdParty/UeSaveGame/   # UE 存档解析库（Apache-2.0）
```

### 设计要点

- **统一节点树**：所有格式解析后都映射到同一棵 `SaveNode` 树，UI 只需处理一种数据结构
- **槽位回写**：每个叶子节点记住自己在原始结构中的位置（偏移 / 键 / 下标），保存时精确回写，不重建整个文档
- **两段式打开**：先尝试直接识别格式，失败才解压缩层 —— 避免 LZString 这类"看起来像 Base64"的格式被误判
- **字节级 round-trip 验证**：每个格式都有测试确保「原样读入再写出」与原始文件逐字节一致

## 已知限制

- 加密存档（需专用密钥）、Oodle 压缩、RocksDB / LevelDB 不支持
- Easy Save 3（Unity 商业插件）需要游戏内 AES 密钥，暂未支持
- UE 存档中的 Vector / Guid / DateTime 等内置结构体为只读
- 只读格式（未知二进制）无法写回

## 第三方组件

| 组件 | 用途 | 许可 |
|------|------|------|
| [UeSaveGame](https://github.com/CrystalFerrai/UeSaveGame) | Unreal 存档解析 | Apache-2.0 |
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) | Zstd 压缩 | MIT |
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | LZ4 压缩 | MIT |
| [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) | BZip2 压缩 | MIT |

## 许可

[MIT](LICENSE)

## 免责声明

本工具仅用于处理**本地存档文件**，不会连接游戏服务器，也不修改联机数据。

修改存档存在风险：可能导致进度损坏、成就失效，或在带完整性校验的游戏中无法读取。请自行备份，并遵守所玩游戏的服务条款。作者不对使用本工具造成的任何损失负责。
