# Built-in Windows exclusion data

The JSON files are sing-box source rule sets embedded in the Windows client.

- The Russian IP ranges are the GeoLite2 country data created by [MaxMind](https://www.maxmind.com), converted by [SagerNet](https://github.com/SagerNet/sing-geoip/blob/66c864fcfe1535549e0658569cb514dbd35756ba/geoip-ru.srs).
- The Russian and Microsoft domain lists come from [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community) under the MIT license, via [SagerNet](https://github.com/SagerNet/sing-geosite/tree/d5d86605baca4b3941a4a4b3403aca36df2f4adf).
- The Microsoft 365 address list contains the distinct IP prefixes published by the [Microsoft 365 worldwide endpoints service](https://learn.microsoft.com/microsoft-365/enterprise/microsoft-365-ip-web-service).
- Regenerate the Cyrillic IDNA matching rules with `python windows/scripts/generate-cyrillic-rules.py`. The other source rule sets can be refreshed with `sing-box rule-set decompile` or, for Microsoft 365, the worldwide endpoints API.
