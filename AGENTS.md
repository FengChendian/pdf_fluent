# 开发约定

- Python 脚本统一通过 `uv run python <script.py>` 启动，不要直接调用 `python` 或 `python3`。
- Avalonia 功能验证优先使用 Avalonia Headless 自动化测试，不要依赖桌面截图判断布局、滚动或虚拟化行为；截图仅用于人工视觉检查。
