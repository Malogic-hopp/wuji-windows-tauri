# 调研：Work-Review 与 Hindsight 项目借鉴

> 调研日期：2026-07-06
> 调研对象：
> - `ref\Work-Review`：Tauri 2 + Svelte + Rust 个人工作回顾工具
> - `ref\Hindsight`：Tauri 2 + React + Rust 屏幕时间/活动追踪工具
> 调研目的：为 WUJI（C# WPF + Agent 双进程本地监控桌面应用）寻找可借鉴的产品设计、架构与工程实践。

---

## 一、项目概况

| 维度 | Work-Review | Hindsight |
|------|-------------|-----------|
| 定位 | 本地优先的个人工作回顾、日报生成 | 本地优先的屏幕时间/活动追踪、AI 日报 |
| 技术栈 | Tauri 2 + Svelte 4 + Rust + SQLite | Tauri 2 + React 19 + TypeScript + Rust + SQLite + llama.cpp |
| 采集粒度 | 前台应用、窗口标题、浏览器 URL、可选截图/OCR | 前台应用、窗口标题、浏览器 URL、可选截图/OCR |
| AI 策略 | 本地模板默认可用，云端模型可选 | 本地 llama.cpp 优先，云端 API 可选 |
| 跨平台 | Windows / macOS / Linux | Windows / macOS |
| 发布方式 | Tauri bundler（NSIS/DMG/deb/AppImage） | Tauri bundler（NSIS/DMG） |

两者与 WUJI 的核心相似点：

- 都是**本地优先**的桌面监控应用，强调数据不上传。
- 都采用**前后端分离**架构：UI 负责展示，后端负责采集、存储、隐私敏感操作。
- 都面临相同的工程挑战：前台窗口采集、空闲检测、截图隐私、数据归档、AI 集成、安装包发布。

---

## 二、值得学习的核心设计

### 1. 本地优先 + 隐私内嵌的产品哲学

两个项目都把“本地优先”放在 README 最显眼位置，并把隐私控制嵌入采集、存储、AI、同步、卸载全链路。

**Work-Review 的做法：**

- 三级隐私动作：`Record` / `Anonymize` / `Skip`。
- 默认忽略密码管理器、银行类应用。
- 关键词脱敏、域名黑名单、OCR 敏感信息过滤。
- AI 模式分档：Local（基础模板）/ Summary（只传摘要）/ Cloud（上传截图）。

**Hindsight 的做法：**

- 截图默认关闭（v0.6.6 重置所有老用户为关）。
- 截图永不上云，即使开启 Google Drive 同步。
- 工作时段设置：非工作时段不采集/不截图。
- 截图前隐私判定 + 截图后复核（TOCTOU 防护）。

**对 WUJI 的启示：**

1. 把隐私策略前置到 Agent 采集层，写入任何数据前先决定 Record/Anonymize/Skip。
2. 如果未来支持截图，必须默认关闭，且明确承诺“截图不上云”。
3. 在 UI 文案和 README 中持续强化“本地优先、AI 可选、数据不上传”。

---

### 2. 双信号空闲检测：键鼠空闲 + 屏幕活跃

Hindsight 把“键鼠空闲”与“屏幕是否变化”解耦，解决了看视频、盯编译等被动观看时段被误判为空闲的问题。

**Hindsight 的实现：**

- 键鼠 idle 超阈值（默认 180s）→ 疑似挂机。
- 屏幕活跃探测：把焦点窗口缩成 256×144 灰度，与上一帧逐像素比较差值。
- 锁屏/息屏/屏保时无条件封口当前会话。
- 睡眠 gap 检测：两次 tick 墙钟间隔 >15s 认为机器睡过。

**Work-Review 的实现：**

- 键鼠超时 + 截图 perceptual hash 连续 3 次相似度 ≥95% 才确认为空闲。

**对 WUJI 的启示：**

1. WUJI 当前 idle/active 判断可引入屏幕变化作为第二信号，避免被动观看时段被中断。
2. 增加“睡眠 gap”检测，防止合盖睡眠被计入某个应用。
3. 锁屏/屏保时立即封口当前 session。

---

### 3. 数据模型：保留原始会话 + 统一聚合入口

两个项目都没有只保存日汇总，而是保留了原始活动会话，便于小时级/应用级/标题级钻取。

**Work-Review 的表设计：**

- `activities`：timestamp、app_name、window_title、browser_url、category、duration、screenshot_path、ocr_text、executable_path。
- `daily_reports` / `hourly_summaries`：按语言缓存日报和小时摘要。
- 使用 SQLite FTS5 全文索引，并通过触发器自动同步。
- 统一过滤入口 `get_daily_stats_with_segments_filtered`，确保多页面统计口径一致。

**Hindsight 的表设计：**

- `activities`：一行 = 一段连续焦点会话，含 `started_at/ended_at/duration_secs/local_date/local_hour/process_name/window_title/category_id/screenshot_path/device_id/remote_id/updated_at/origin`。
- `categories` + `super_categories` + `app_groups`：分类体系 + 跨 OS 应用合并。
- `app_icons`：图标 PNG 字节落库，跨设备同步。
- `memory.sqlite`：独立数据库存放 OCR/屏幕记忆/聊天历史。
- `sync_outbox` / `sync_cursor` / `devices`：同步基础设施。

**对 WUJI 的启示：**

1. 保留原始 `sessions` 行级记录，不要只保存日汇总。
2. 在 WUJI SQLite 中增加 FTS5 索引，支持窗口标题/OCR 文本搜索。
3. 引入 `app_groups` 概念，把同名/不同进程应用合并成组。
4. 图标字节落库，便于后续跨设备同步。
5. 如未来做截图/OCR，用独立 `memory.sqlite` 存放派生/高敏感数据。
6. 即使当前不上云，也预留 `device_id`、`remote_id`、`updated_at`、`deleted_at` 字段。

---

### 4. AI 集成：默认可用、可选增强、数据最小化

两个项目都把 AI 定位为“增强可读性”而非“使用前提”。

**Work-Review 的 AI 模式：**

- Local：基础模板，零配置。
- Summary：只传摘要。
- Cloud：上传截图到云端模型。
- Agent 架构：Tools → Model → Executor → Orchestrator，支持路由 + 自动降级。

**Hindsight 的 AI 模式：**

- 本地 llama.cpp server 作为 OpenAI 兼容端点。
- AI 日报基于 `activities` 合成“逐小时活动时间线 + top apps + 窗口标题样例”，单步送 LLM。
- Chat 工具化：只读数据库工具 `query_stats` / `search_text` / `get_timeline`，输出证据卡。

**对 WUJI 的启示：**

1. AI 日报/助手默认使用规则模板，配置模型后才启用 AI 增强。
2. AI 日报优先用“活动时间线 + 纯文本 LLM”，不要一上来用昂贵的多模态方案。
3. Chat 提供固定只读工具，并用证据卡限制幻觉。
4. 发送给 LLM 的内容先经过隐私过滤。

---

### 5. 工程实践：测试、CI/CD、发布流程

两个项目都有较成熟的工程闭环。

**Work-Review：**

- Rust workspace 拆分：`crates/core`、`crates/mcp-server`、`crates/skills-engine`、`src-tauri`。
- 前端用 Node.js 内置 `node --test`，文件命名为 `*.test.js`。
- CI 在 `v*` tag 触发，矩阵构建 macOS/Windows/Linux。
- 版本三处一致：`package.json`、`Cargo.toml`、`tauri.conf.json`。
- CHANGELOG 遵循 Keep a Changelog + SemVer。

**Hindsight：**

- CI 分两 job：frontend lint/type-check/test/build → Rust rustfmt/clippy/test。
- Release 脚本 `scripts/release.mjs` 校验干净 main、版本一致、release notes 存在、tag 不存在。
- 每个版本一份 `docs/release-notes/vX.Y.Z.md`。
- 国际化：i18next，支持 5 种语言。

**对 WUJI 的启示：**

1. 引入 GitHub Actions：push `v*` tag 后自动 build/test/pack。
2. 建立 `docs/release-notes/vX.Y.Z.md` 机制。
3. 增加版本一致性校验脚本。
4. WPF/App 与 Agent/Core 测试分离，CI 先 `dotnet test` 再打包。
5. 尽早埋点 i18n，避免后期全量替换中文文案。

---

### 6. 安装包/发布：per-user、进程清理、未签名体验

**Hindsight 的 Windows 发布：**

- Tauri NSIS，`installMode: "currentUser"`（安装到 `%LOCALAPPDATA%`，无需管理员）。
- NSIS hooks：安装前 taskkill 解决托盘文件锁；卸载前强杀进程；卸载后询问是否删除用户数据（默认 No）。
- README 明确告知 SmartScreen 绕过步骤。

**Work-Review 的发布：**

- Tauri bundler 生成 NSIS/DMG/deb/AppImage + portable zip。
- 自动更新：Tauri updater + GitHub Release + 国内镜像 fallback。
- macOS 签名 + notarize，Windows 证书 thumbprint 可配置。

**对 WUJI 的启示：**

1. WPF + Agent 双进程安装包建议用 per-user 安装（`%LOCALAPPDATA%\WUJI`）。
2. 安装/升级前加入 PreInstall hook：先 kill Agent/主程序，避免文件锁。
3. 卸载时检测是否删除 `%LocalAppData%\WUJI` 数据，默认保留。
4. README 提前写好未签名安装指引（SmartScreen → More info → Run anyway）。
5. 未来引入自动更新（Velopack/NetSparkle），更新元数据放 GitHub Release。

---

### 7. 产品体验：时间线、日报、分类

**Work-Review 的体验：**

- 日报用 `<!-- WR_BLOCK_START:xxx -->` 占位符包裹统计块，读取时用最新 stats 重渲染。
- 支持段落级钉选/隐藏/编辑、AI 编排顺序。
- 分类：基础分类 + 中文语义分类 + 用户自定义 + 网站语义规则。
- 工作时段支持多段（如上午 + 下午）。

**Hindsight 的体验：**

- Today 页：24 小时堆叠柱状图，点击柱子钻取该小时 top apps。
- 应用排行按 `app_group` 聚合。
- 大类容器（工作/娱乐/社交/浏览）用于顶层占比。
- AI 日报按自定义时段分段生成。

**对 WUJI 的启示：**

1. 把“24 小时时间线”作为首页核心视图。
2. 应用排行做组聚合，不要一个进程名一行。
3. 引入大类层（工作/学习/娱乐/其他）。
4. 日报采用“模板块 + 占位符 + 实时重渲染”，避免数字陈旧。
5. 支持多段工作时间配置。

---

## 三、对 WUJI 的优先级建议

结合 WUJI 当前阶段（阶段 12 安装包与发布体验 MVP），建议按以下优先级吸收：

### 立即可做（阶段 12 范围内）

1. **安装包 per-user + 卸载保留数据**
   - 参考 Hindsight NSIS hooks，WUJI 安装包默认安装到 `%LocalAppData%\Programs\WUJI`。
   - 卸载前检测运行进程，卸载时默认保留 `%LocalAppData%\WUJI` 数据。

2. **README 未签名安装指引**
   - 参考 Hindsight，提前写好 SmartScreen 绕过步骤。

3. **版本一致性与 release notes**
   - 建立 `docs/release-notes/vX.Y.Z.md`。
   - 校验 `Directory.Build.props`、安装包版本一致。

### 短期建议（阶段 13-15）

4. **隐私策略前置到采集层**
   - 在 Agent 写入任何数据前过 PrivacyFilter，决定 Record/Anonymize/Skip。
   - 设置页增加应用级隐私规则（正常 / 仅统计 / 忽略）。

5. **双信号空闲检测**
   - 键鼠空闲 + 屏幕变化探测，解决被动观看时段被误判。

6. **数据模型增强**
   - 保留原始 `sessions` 记录。
   - 增加 FTS5 索引、`app_groups`、图标落库。

### 中期建议（后续大版本）

7. **AI 助手/日报**
   - 默认规则模板，配置模型后启用 AI 增强。
   - 优先用“活动时间线 + 纯文本 LLM”。
   - Chat 工具化，提供只读数据库工具。

8. **自动更新**
   - 引入 Velopack 或 NetSparkle。
   - 用 GitHub Actions 自动构建发布。

9. **跨设备同步预留**
   - 数据模型加入 `device_id`、`remote_id`、`updated_at`、`deleted_at`。
   - 即使当前不上云，也让后续扩展零迁移。

---

## 四、风险与注意事项

1. **不要照搬技术栈**：Work-Review/Hindsight 用 Tauri + Rust，WUJI 用 C# WPF + Agent，借鉴的是设计思想而非具体代码。
2. **隐私合规**：截图/OCR 功能必须默认关闭，且明确告知用户数据使用范围。
3. **性能**：屏幕变化探测、OCR、本地 LLM 都会显著增加 CPU/内存开销，需做开关和降级。
4. **跨平台**：WUJI 当前聚焦 Windows，但建议把平台相关代码抽到独立命名空间，为未来预留接口。

---

## 五、参考文件

```text
ref\Work-Review\README.md
ref\Work-Review\README.zh.md
ref\Work-Review\Cargo.toml
ref\Work-Review\crates\core\src\*
ref\Work-Review\src-tauri\src\*
ref\Hindsight\README.md
ref\Hindsight\docs\design\screen-memory.md
ref\Hindsight\src-tauri\src\*
ref\Hindsight\src\api\hindsight.ts
ref\Hindsight\.github\workflows\ci.yml
ref\Hindsight\.github\workflows\release.yml
```

---

## 六、结论

Work-Review 和 Hindsight 都是本地优先监控类桌面应用的优秀参考。两者最值得 WUJI 学习的不是某项具体技术，而是：

- **本地优先 + 隐私内嵌**的产品哲学
- **采集层隐私前置**的工程实践
- **键鼠空闲 + 屏幕活跃**的双信号空闲检测
- **原始会话保留 + 统一聚合入口**的数据模型
- **AI 默认可用、可选增强、数据最小化**的 AI 策略
- **per-user 安装 + 卸载保留数据 + 未签名指引**的发布体验

建议 WUJI 优先在产品文档和阶段 12 安装包体验中吸收发布/卸载经验，再在后续版本中逐步引入隐私策略、双信号空闲检测和数据模型增强。
