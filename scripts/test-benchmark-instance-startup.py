"""Deterministic command-generation tests; does not start Java or access a real instance."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('startup_benchmark', Path(__file__).with_name('benchmark-instance-startup.py'))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class ArgumentTests(unittest.TestCase):
    def test_windows_rules_exclude_other_os_and_architectures(self):
        for target in ({'name': 'linux'}, {'name': 'osx'}, {'arch': 'x86'}, {'arch': 'aarch64'}):
            self.assertFalse(benchmark.allowed({'rules': [{'action': 'allow', 'os': target}]}))
        self.assertTrue(benchmark.allowed({'rules': [{'action': 'allow', 'os': {'name': 'windows'}}]}))

    def test_rule_order_and_false_features(self):
        self.assertFalse(benchmark.allowed({'rules': [{'action': 'allow'}, {'action': 'disallow'}]}))
        self.assertTrue(benchmark.allowed({'rules': [{'action': 'allow', 'features': {'demo': False}}]}))
        self.assertFalse(benchmark.allowed({'rules': [{'action': 'allow', 'features': {'demo': True}}]}))

    def test_expand_resolution_but_not_quickplay_or_demo(self):
        parts = ['--gameDir', '${game}',
                 {'rules': [{'action': 'allow', 'features': {'has_custom_resolution': True}}],
                  'value': ['--width', '${width}']},
                 {'rules': [{'action': 'allow', 'features': {'is_quick_play_singleplayer': True}}],
                  'value': ['--quickPlaySingleplayer', '${unavailable_world}']},
                 {'rules': [{'action': 'allow', 'features': {'is_demo_user': True}}], 'value': '--demo'}]
        self.assertEqual(benchmark.expand_arguments(parts, {'game': '中文 [实例]', 'width': '854'},
                         {'has_custom_resolution': True}), ['--gameDir', '中文 [实例]', '--width', '854'])

    def test_unknown_placeholder_fails_instead_of_silently_omitting(self):
        with self.assertRaisesRegex(ValueError, 'Unresolved'):
            benchmark.expand_arguments(['${unknown}'], {})

    def test_coordinate_classifiers_and_extensions(self):
        self.assertEqual(benchmark.artifact_path({'name': 'g.test:demo:1:api'}), 'g/test/demo/1/demo-1-api.jar')
        self.assertEqual(benchmark.artifact_path({'name': 'g:demo:1@zip'}), 'g/demo/1/demo-1.zip')
        self.assertEqual(benchmark.artifact_path({'name': 'bad', 'downloads': {'artifact': {'path': 'exact.jar'}}}), 'exact.jar')
        with self.assertRaises(ValueError):
            benchmark.artifact_path({'name': 'bad'})


class CommandTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='startup-command-tests-', dir=os.environ.get('PI_SCRATCH_DIR'))
        self.root = Path(self.temp.name)
        self.instance = self.root / 'isolated/versions/肉鸽生化[导入包] v1.0'
        self.instance.mkdir(parents=True)
        self.shared = self.root / 'shared'
        self.library = self.shared / 'libraries/g/loader/1/loader-1-api.jar'
        self.library.parent.mkdir(parents=True)
        with zipfile.ZipFile(self.library, 'w') as jar:
            jar.writestr('natives/windows/x64/native.dll', b'native')
            jar.writestr('../do-not-extract.txt', b'not native')
        (self.instance / (self.instance.name + '.jar')).write_bytes(b'client')
        (self.instance / 'mods').mkdir()
        (self.instance / 'mods/a.jar').write_bytes(b'mod fixture')
        self.output = self.root / 'output'
        self.output.mkdir()
        self.doc = {
            'id': self.instance.name, 'mainClass': 'cpw.mods.bootstraplauncher.BootstrapLauncher',
            'javaVersion': {'majorVersion': 17}, 'assetIndex': {'id': '5'},
            'libraries': [{'name': 'g:loader:1:api'}],
            'arguments': {'jvm': ['-DignoreList=bootstraplauncher,${version_name}.jar',
                                  '-p', '${library_directory}/g/loader/1/loader-1-api.jar',
                                  '--add-modules', 'ALL-MODULE-PATH', '-cp', '${classpath}',
                                  '-Djava.library.path=${natives_directory}'],
                          'game': ['--username', '${auth_player_name}', '--gameDir', '${game_directory}',
                                   '--accessToken', '${auth_access_token}', '--launchTarget', 'forgeclient']}}
        self.save()

    def tearDown(self):
        self.temp.cleanup()

    def save(self):
        (self.instance / (self.instance.name + '.json')).write_text(json.dumps(self.doc), encoding='utf-8')

    def build(self, **kwargs):
        return benchmark.build_command(self.instance, Path('java.exe'), self.output,
                                       minecraft_root=self.shared, **kwargs)

    def test_forge_module_options_and_isolated_root_preserved(self):
        command, metadata = self.build()
        self.assertEqual(command[command.index('--launchTarget') + 1], 'forgeclient')
        self.assertEqual(command[command.index('-p') + 1], self.library.as_posix())
        self.assertIn('--add-modules', command)
        self.assertIn('ALL-MODULE-PATH', command)
        self.assertIn(self.instance.name, ' '.join(command))
        self.assertIn(str(self.library), command[command.index('-cp') + 1])
        self.assertEqual(command[command.index('--accessToken') + 1], '0')
        self.assertEqual(metadata['java_required'], 17)
        self.assertEqual(metadata['top_level_mod_count'], 1)
        self.assertEqual(len(metadata['mod_set_sha256']), 64)
        self.assertEqual((self.output / 'natives/native.dll').read_bytes(), b'native')
        self.assertFalse((self.output / 'do-not-extract.txt').exists())

    def test_native_paths_cannot_escape_output_directory(self):
        with zipfile.ZipFile(self.library, 'a') as jar:
            jar.writestr('..\\escaped.dll', b'flattened')
        self.build()
        self.assertEqual((self.output / 'natives/escaped.dll').read_bytes(), b'flattened')
        self.assertFalse((self.output / 'escaped.dll').exists())

    def test_explicit_heap_and_diagnostic_agent_only_when_requested(self):
        command, _ = self.build(heap='6G', jvm_args=['-Ddiagnostic=true'], agent=self.root / 'agent.jar')
        self.assertIn('-Xmx6G', command)
        self.assertIn('-Ddiagnostic=true', command)
        self.assertTrue(any(part.startswith('-javaagent:') for part in command))

    def test_unknown_game_argument_refused(self):
        self.doc['arguments']['game'].append('${missing}')
        self.save()
        with self.assertRaisesRegex(ValueError, 'Unresolved'):
            self.build()

    def test_world_or_server_entry_refused(self):
        self.doc['arguments']['game'] += ['--server', 'example.invalid']
        self.save()
        with self.assertRaisesRegex(ValueError, 'Automatic world/server'):
            self.build()

    def test_missing_libraries_fail_without_starting_or_downloading(self):
        self.library.unlink()
        with self.assertRaises(FileNotFoundError):
            self.build()

    def test_inherited_json_is_explicitly_unsupported(self):
        self.doc['inheritsFrom'] = 'parent'
        self.save()
        with self.assertRaisesRegex(ValueError, 'inherited/legacy'):
            self.build()


if __name__ == '__main__':
    unittest.main()
