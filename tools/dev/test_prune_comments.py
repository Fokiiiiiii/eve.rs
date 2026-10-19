import importlib.util
import unittest
from pathlib import Path

MODULE = Path(__file__).with_name('prune-comments.py')
SPEC = importlib.util.spec_from_file_location('prune_comments', MODULE)
pruner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pruner)


class PruneCommentsTests(unittest.TestCase):
    def check(self, source, language):
        cleaned, _, removed = pruner.transform(source, language)
        self.assertEqual(pruner.code_tokens(source, language), pruner.code_tokens(cleaned, language))
        self.assertGreater(removed, 0)

    def test_rust_raw_strings_nested_comments_and_attributes(self):
        self.check('''#[allow(dead_code)]
/// TODO: stale
fn f() { let s = r###"// intact /* intact */"###; /* outer /* inner */ tail */ }
''', 'rs')

    def test_rust_lifetime_does_not_hide_comments(self):
        self.check("fn f<'a>(x: &'a str) { // TODO: stale\n let _ = x; }\n", 'rs')

    def test_csharp_verbatim_interpolated_raw_and_directive(self):
        self.check('''#nullable enable
/// <summary>TODO: stale</summary>
class C { string s = $@"{{ /* intact */ }}"; string r = $$"""// intact {value}"""; // TODO: stale
}
''', 'cs')

    def test_python_hash_in_string(self):
        self.check('''value = "# intact"
# TODO: stale
''', 'py')

    def test_doc_comment_has_no_empty_line_before_item(self):
        src = '/// Keep docs.\n\n#[inline]\nfn f() {}\n'
        result, _, _ = pruner.transform(src, 'rs')
        self.assertIn('/// Keep docs.\n#[inline]', result)

    def test_deleted_comment_collapses_blank_lines(self):
        src = 'let a = 1;\n\n// TODO: stale\n\n\nlet b = 2;\n'
        result, _, _ = pruner.transform(src, 'rs')
        self.assertNotIn('\n\n\n', result)

    def test_short_explanatory_comment_survives(self):
        src = 'fn f() { // Keep this because ordering matters.\n call(); }\n'
        result, _, removed = pruner.transform(src, 'rs')
        self.assertEqual(removed, 0)
        self.assertEqual(pruner.code_tokens(src, 'rs'), pruner.code_tokens(result, 'rs'))


if __name__ == '__main__':
    unittest.main()
