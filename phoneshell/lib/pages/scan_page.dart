import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:image_picker/image_picker.dart';
import 'package:google_mlkit_barcode_scanning/google_mlkit_barcode_scanning.dart' as mlkit;

import '../core/constants.dart';
import '../core/i18n.dart';

class ScanPage extends StatefulWidget {
  const ScanPage({super.key});

  @override
  State<ScanPage> createState() => _ScanPageState();
}

class _ScanPageState extends State<ScanPage> {
  final MobileScannerController _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
    formats: [BarcodeFormat.qrCode],
    cameraResolution: const Size(1280, 720),
  );
  bool _isProcessing = false;
  String _status = '';

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  Future<void> _handleGallery() async {
    if (_isProcessing) return;
    setState(() {
      _isProcessing = true;
      _status = _t('正在解析相册二维码...', 'Analyzing image...');
    });

    try {
      final picker = ImagePicker();
      final image = await picker.pickImage(source: ImageSource.gallery);
      if (image == null) {
        setState(() {
          _isProcessing = false;
          _status = '';
        });
        return;
      }

      final scanner = mlkit.BarcodeScanner();
      final input = mlkit.InputImage.fromFilePath(image.path);
      final barcodes = await scanner.processImage(input);
      await scanner.close();

      if (!mounted) return;
      if (barcodes.isNotEmpty && barcodes.first.rawValue != null) {
        Navigator.of(context).pop(barcodes.first.rawValue);
        return;
      }
      setState(() {
        _isProcessing = false;
        _status = _t('未识别到二维码', 'No QR code found');
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _isProcessing = false;
        _status = _t('解析失败', 'Failed to analyze image');
      });
    }
  }

  void _onDetect(BarcodeCapture capture) {
    if (_isProcessing) return;
    for (final barcode in capture.barcodes) {
      final value = barcode.rawValue;
      if (value != null && value.isNotEmpty) {
        _isProcessing = true;
        Navigator.of(context).pop(value);
        return;
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final padding = MediaQuery.of(context).padding;
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        children: [
          MobileScanner(
            controller: _controller,
            onDetect: _onDetect,
          ),
          Positioned(
            top: 40 + padding.top,
            left: 20 + padding.left,
            child: Text(
              _t('扫描二维码', 'Scan QR Code'),
              style: const TextStyle(color: Colors.white, fontSize: 16),
            ),
          ),
          if (_status.isNotEmpty)
            Positioned(
              top: 80 + padding.top,
              left: 20 + padding.left,
              right: 20 + padding.right,
              child: Text(
                _status,
                style: const TextStyle(color: Colors.white70, fontSize: 12),
              ),
            ),
          Positioned(
            left: 0,
            right: 0,
            bottom: 0,
            child: Container(
              padding: EdgeInsets.fromLTRB(
                20 + padding.left,
                14,
                20 + padding.right,
                14 + padding.bottom,
              ),
              color: Colors.black.withOpacity(0.6),
              child: Row(
                children: [
                  Expanded(
                    child: TextButton(
                      onPressed: _isProcessing ? null : _handleGallery,
                      style: TextButton.styleFrom(
                        foregroundColor: Colors.white,
                      ),
                      child: Text(_t('相册', 'Album')),
                    ),
                  ),
                  Expanded(
                    child: TextButton(
                      onPressed: () => Navigator.of(context).pop(),
                      style: TextButton.styleFrom(
                        foregroundColor: const Color(AppColors.accent),
                      ),
                      child: Text(_t('取消', 'Cancel')),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}
