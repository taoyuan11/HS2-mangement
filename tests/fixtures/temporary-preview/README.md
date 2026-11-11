# 临时预览归档测试样本

`plain.zip`、`plain.7z`、`encrypted.zip`、`encrypted.7z` 仅含自行生成的 `fixture.txt`，使用 7-Zip 创建；测试密码为 `preview-pass`。`plain-rar5.rar` 按 RAR5 官方格式生成未压缩条目，内容相同。

`encrypted.rar` 是 libarchive 的加密 RAR 测试样本，密码为 `12345678`，包含 `foo.txt`、`bar.txt`。只供测试，不随应用发布。许可见同目录 `libarchive-fixture-license.txt`。固定上游提交：d0bf2da07ebaa3964090ee936edea538d1602449

来源：https://raw.githubusercontent.com/libarchive/libarchive/d0bf2da07ebaa3964090ee936edea538d1602449/libarchive/test/test_read_format_rar_encryption_data.rar.uu

RAR5 格式：https://www.rarlab.com/technote.htm
