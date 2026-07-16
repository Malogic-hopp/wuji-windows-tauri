# Bridge dev sidecar

这里仅接收由 `pnpm bridge:prepare` 生成的本地开发输出：

```text
sidecars/bridge/QuantifiedSelf.Windows.Client.Bridge.exe
```

输出目录已被 Git 忽略。Rust Shell 只使用这个固定位置，并固定传入 `--channel dev`；React 无法提供可执行文件、路径或 channel。
