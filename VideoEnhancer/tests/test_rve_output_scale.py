"""验证 GPU Lanczos4 与 CPU 参考采样的数值、尺寸和设备。"""
import importlib.util
import unittest
from pathlib import Path

import cv2
import numpy as np
import torch

path = Path(__file__).parents[1] / "Backend/embedded-tools/rve_output_scale.py"
spec = importlib.util.spec_from_file_location("rve_output_scale", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class OutputScaleTests(unittest.TestCase):
    def test_reference_and_device(self):
        image = np.random.default_rng(7).random((37, 53, 3), dtype=np.float32)
        tensor = torch.from_numpy(image).permute(2, 0, 1).unsqueeze(0).to("cpu")
        for width, height in ((26, 18), (79, 55), (53, 18), (26, 37)):
            with self.subTest(size=(width, height)):
                result = module.resize_tensor(tensor, width, height)
                self.assertEqual(result.device, tensor.device)
                expected = cv2.resize(image, (width, height), interpolation=cv2.INTER_LANCZOS4)
                actual = result[0].permute(1, 2, 0).cpu().numpy()
                np.testing.assert_allclose(actual, expected, atol=1e-5)

    @unittest.skipUnless(torch.cuda.is_available(), "需要 CUDA GPU")
    def test_cuda_retains_device(self):
        image = torch.full((1, 3, 31, 47), 0.5, device="cuda")
        result = module.resize_tensor(image, 23, 15)
        self.assertEqual(result.device, image.device)
        torch.testing.assert_close(result, torch.full_like(result, 0.5))

    def test_half_precision_constant_and_same_size(self):
        tensor = torch.full((2, 3, 31, 47), 0.25, device="cpu", dtype=torch.float16)
        self.assertIs(module.resize_tensor(tensor, 47, 31), tensor)
        result = module.resize_tensor(tensor, 23, 15)
        self.assertEqual(result.dtype, tensor.dtype)
        self.assertEqual(result.device, tensor.device)
        self.assertEqual(tuple(result.shape), (2, 3, 15, 23))
        torch.testing.assert_close(result, torch.full_like(result, 0.25))


if __name__ == "__main__":
    unittest.main()
