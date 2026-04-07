import 'package:flutter/material.dart';

import '../core/constants.dart';

class PhoneShellHeaderBar extends StatelessWidget {
  const PhoneShellHeaderBar({
    super.key,
    required this.subtitle,
    required this.padding,
    required this.trailing,
    this.height = 56,
    this.showTopAccent = true,
    this.backgroundColor = const Color(AppColors.surface1),
    this.brandTopOffset = -2,
  });

  final String subtitle;
  final EdgeInsetsGeometry padding;
  final Widget trailing;
  final double height;
  final bool showTopAccent;
  final Color backgroundColor;
  final double brandTopOffset;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        if (showTopAccent)
          Container(height: 2, color: const Color(AppColors.accent)),
        Container(
          height: height,
          color: backgroundColor,
          padding: padding,
          child: Row(
            children: [
              Expanded(
                child: Transform.translate(
                  offset: Offset(0, brandTopOffset),
                  child: PhoneShellBrandBlock(subtitle: subtitle),
                ),
              ),
              const SizedBox(width: 10),
              Flexible(
                child: Align(alignment: Alignment.centerRight, child: trailing),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class PhoneShellBrandBlock extends StatelessWidget {
  const PhoneShellBrandBlock({super.key, required this.subtitle});

  final String subtitle;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Image.asset(
          'assets/images/phoneshell_header.png',
          width: AppSizes.fontSizeSubtitle,
          height: AppSizes.fontSizeSubtitle,
        ),
        const SizedBox(width: 8),
        Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              AppStrings.appName,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontSize: 20,
                height: 1.05,
                color: Color(AppColors.textPrimary),
                fontWeight: FontWeight.bold,
              ),
            ),
            Text(
              subtitle,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontSize: 11,
                height: 1.1,
                color: Color(AppColors.textMuted),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class PhoneShellInfoChip extends StatelessWidget {
  const PhoneShellInfoChip({super.key, required this.text, this.fontSize = 14});

  final String text;
  final double fontSize;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: const Color(AppColors.highlight),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Text(
        text,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        textAlign: TextAlign.end,
        style: TextStyle(
          fontSize: fontSize,
          color: const Color(AppColors.textPrimary),
          fontWeight: FontWeight.w500,
        ),
      ),
    );
  }
}
