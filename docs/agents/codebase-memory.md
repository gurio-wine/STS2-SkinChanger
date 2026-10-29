# codebase-memory 索引注意事项

- 索引时明确传入仓库绝对路径，不索引整个用户目录。
- 索引时必须显式传 `name: STS2-SkinChanger`：只给 `repo_path`（或 CLI 同理）会另建一个 `C-Users-...` 形式的重复项目，误建的要删掉。
- 索引是增量的，但过期索引会静默漏掉新文件——查不到刚提交的符号时先重建索引，不要断言该符号不存在。
- `check_index_coverage` 只报告已知失败：某个文件根本没入图时它同样显示 `no_recorded_issue`，不能当作“已入图”的证据，只认符号查询结果。
- `detect_changes` 的 `base_branch` 默认是 `main`，本仓库没有该分支，会静默返回 0 变更（假阴性）；必须显式传 `master`。改完代码可用 `detect_changes(since=HEAD~1, base_branch=master)` 自查影响面。
