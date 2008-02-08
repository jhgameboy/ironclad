
import os
import shutil
import subprocess
import tempfile
import unittest
from tests.utils.runtest import makesuite, run


def GetPexportsLines(path):
    stream = os.popen("pexports %s" % path.replace('/cygdrive/c', 'c:'))
    try:
        return set(map(lambda s: s.strip(), stream.readlines()))
    finally:
        stream.close()


class BuildStubTest(unittest.TestCase):

    def testBuildStubWithBadParams(self):
        retVal = subprocess.call([
            "python", "tools/buildstub.py"])
        self.assertEquals(retVal, 1, "buildstub didn't bail for 0 param")
        retVal = subprocess.call([
            "python", "tools/buildstub.py", "one"])
        self.assertEquals(retVal, 1, "buildstub didn't bail for 1 param")
        retVal = subprocess.call([
            "python", "tools/buildstub.py", "one", "two", "three", "four"])
        self.assertEquals(retVal, 1, "buildstub didn't bail for 4 param")


    def testBuildStubCreatesOutputDll(self):
        inputPath = "tests/data/exportsymbols.dll"
        overridePath = "tests/data/overrides"
        tempDir = tempfile.gettempdir()
        ourTempDir = os.path.join(tempDir, 'buildstubtest')

        def testGenerates(extraArgs):
            if os.path.exists(ourTempDir):
                shutil.rmtree(ourTempDir)

            args = ["python", "tools/buildstub.py"]
            args.extend(extraArgs)
            retVal = subprocess.call(args)
            self.assertEquals(retVal, 0, "process ended badly")
            outputPath = os.path.join(ourTempDir, "exportsymbols.dll")
            self.assertTrue(os.path.exists(outputPath))

            inputLines = GetPexportsLines(inputPath)
            outputLines = GetPexportsLines(outputPath)

            inputLines |= set(["init", "jumptable DATA"])
            self.assertEquals(outputLines, inputLines, "bad output symbols")

        testGenerates([inputPath, ourTempDir])
        testGenerates([inputPath, ourTempDir, overridePath])


class Python25StubTest(unittest.TestCase):

    def testPython25Stub(self):
        f = open("tests/data/python25-pexports")
        try:
            python25exports = set(map(lambda s: s.strip(), f.readlines()))
        finally:
            f.close()

        python25exports |= set(["init", "jumptable DATA"])
        generatedExports = GetPexportsLines("build/python25.dll")

        self.assertEquals(generatedExports, python25exports,
                          "build product wrong")


suite = makesuite(BuildStubTest, Python25StubTest)

if __name__ == '__main__':
    run(suite)
