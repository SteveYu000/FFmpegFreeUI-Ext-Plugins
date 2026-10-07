"""验证固定 4x 网络的低倍率图内输出，保留权重与 CUDA 设备。"""
import ast
import unittest
from pathlib import Path

import torch
import torch.nn.functional as F


path = Path(__file__).parents[1] / "Backend/embedded-tools/convert_tensorrt.py"
tree = ast.parse(path.read_text(encoding="utf-8-sig"))
wrapper = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "FinalOutputScale")
namespace = {"torch": torch, "F": F}
exec(compile(ast.Module(body=[wrapper], type_ignores=[]), str(path), "exec"), namespace)
FinalOutputScale = namespace["FinalOutputScale"]


class NativeFour(torch.nn.Module):
    def forward(self, image):
        return F.interpolate(image, scale_factor=4, mode="nearest")


class TensorRtOutputScaleTests(unittest.TestCase):
    def check_outputs(self, device):
        image = torch.rand((1, 3, 12, 16), device=device)
        model = NativeFour().to(device)
        for scale in (2, 3):
            with self.subTest(device=device, scale=scale):
                output_model = FinalOutputScale(model, 12 * scale, 16 * scale)
                output = output_model(image)
                self.assertIs(output_model.model, model)
                self.assertEqual(tuple(output.shape), (1, 3, 12 * scale, 16 * scale))
                self.assertEqual(output.device, image.device)
                expected = F.interpolate(model(image), size=(12 * scale, 16 * scale), mode="bicubic", align_corners=False)
                torch.testing.assert_close(output, expected)
                exported = torch.export.export(output_model, (image,))
                self.assertIn("upsample_bicubic2d", str(exported.graph))
                torch.testing.assert_close(exported.module()(image), expected)

    def test_graph_output(self):
        self.check_outputs("cpu")

    @unittest.skipUnless(torch.cuda.is_available(), "需要 CUDA GPU")
    def test_gpu_output(self):
        self.check_outputs("cuda")


if __name__ == "__main__":
    unittest.main()
