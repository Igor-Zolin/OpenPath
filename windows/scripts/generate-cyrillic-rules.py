"""Generate sing-box regexes for Cyrillic DNS labels in Unicode and IDNA wire form."""
import json
from collections import defaultdict
from pathlib import Path

DIGITS = "abcdefghijklmnopqrstuvwxyz0123456789"


def digit_range(low, high, position=0):
    """Match a contiguous interval of RFC 3492 first-delta integers, least digit first."""
    # The initial bias is 72, so the thresholds for k=36,72,108,... are 1,1,26,...
    threshold = 1 if position < 2 else 26
    base = 36 - threshold
    groups = defaultdict(str)
    for digit, char in enumerate(DIGITS):
        if digit < threshold:
            if low <= digit <= high:
                groups[""] += char
        else:
            first = max(0, (low - digit + base - 1) // base)
            last = (high - digit) // base
            if first <= last:
                groups[digit_range(first, last, position + 1)] += char
    options = [(chars if len(chars) == 1 else f"[{chars}]") + tail
               for tail, chars in groups.items()]
    return options[0] if len(options) == 1 else "(" + "|".join(options) + ")"


def main():
    patterns = [r"[\p{Cyrillic}]"]
    # A Cyrillic/ASCII label's first inserted code point is its smallest non-ASCII
    # character. Its initial delta also encodes the number and position of basic chars.
    for count in range(57):
        width = count + 1
        delta = digit_range((0x400 - 128) * width, (0x530 - 128) * width - 1)
        prefix = f"[a-z0-9-]{{{count}}}-" if count else ""
        patterns.append(r"(?i)(^|\.)xn--" + prefix + delta + r"[a-z0-9]*(\.|$)")
    rules = [{"domain_suffix": ["ru", "su", "moscow", "tatar", "yandex", "xn--p1ai"],
              "domain_regex": patterns}]
    target = Path(__file__).resolve().parents[1] / "src/SshEnv.Core/ExclusionData/cyrillic-domains.json"
    target.write_text(json.dumps({"version": 1, "rules": rules}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
