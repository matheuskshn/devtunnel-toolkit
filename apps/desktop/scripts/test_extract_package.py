import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("extract_package", Path(__file__).with_name("extract-package.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ExtractionTests(unittest.TestCase):
    def test_regular_package_extracts(self):
        with tempfile.TemporaryDirectory(prefix="desktop-extract-") as temporary:
            archive = Path(temporary) / "package.tar.gz"
            destination = Path(temporary) / "out"
            with tarfile.open(archive, "w:gz") as output:
                info = tarfile.TarInfo("./public.txt")
                info.size = 6
                output.addfile(info, io.BytesIO(b"public"))
            module.extract_package(archive, destination)
            self.assertEqual((destination / "public.txt").read_bytes(), b"public")

    def test_links_and_traversal_are_rejected_before_writes(self):
        for name, kind in [("../escape", tarfile.REGTYPE), ("/absolute", tarfile.REGTYPE),
                           ("link", tarfile.SYMTYPE), ("link", tarfile.LNKTYPE), ("device", tarfile.CHRTYPE)]:
            with self.subTest(name=name, kind=kind), tempfile.TemporaryDirectory(prefix="desktop-extract-") as temporary:
                archive = Path(temporary) / "package.tar.gz"
                destination = Path(temporary) / "out"
                with tarfile.open(archive, "w:gz") as output:
                    info = tarfile.TarInfo(name)
                    info.type = kind
                    info.linkname = "../outside"
                    output.addfile(info)
                with self.assertRaises(ValueError):
                    module.extract_package(archive, destination)
                self.assertFalse(destination.exists())

    def test_zip_traversal_and_duplicate_paths_are_rejected(self):
        for name in ["../escape", "/absolute", "C:/drive", "folder\\escape"]:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="desktop-extract-") as temporary:
                archive = Path(temporary) / "package.zip"
                destination = Path(temporary) / "out"
                with zipfile.ZipFile(archive, "w") as output:
                    output.writestr(name, "public")
                with self.assertRaises(ValueError):
                    module.extract_package(archive, destination)
                self.assertFalse(destination.exists())
        with self.assertRaises(ValueError):
            module.validate_members([("./same", 1, True), ("same", 1, True)])


if __name__ == "__main__":
    unittest.main()
