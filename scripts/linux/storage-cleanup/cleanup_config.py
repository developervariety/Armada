"""Shared configuration loader for the host storage-cleanup scripts.

Every host-specific value (roots, exclusions, home directory, lock and receipt
paths, database patterns, container names, image tag patterns, retention) lives
in one JSON file. Each stage reads its own section and fails closed when a
required key is missing, so a stage never falls back to a guessed host path.
"""
import json

DEFAULT_CONFIG_PATH = '/etc/armada/storage-cleanup.json'


class ConfigError(ValueError):
    """The configuration file is unreadable, malformed, or missing a required key."""


def add_config_argument(parser):
    parser.add_argument('--config', default=DEFAULT_CONFIG_PATH,
                        help='JSON configuration file (default: ' + DEFAULT_CONFIG_PATH + ')')


def load_config(path, section, required=(), defaults=None):
    """Return one section merged over its defaults. Keys that start with '_' are comments."""
    try:
        with open(path) as handle:
            data = json.load(handle)
    except FileNotFoundError as error:
        raise ConfigError('Storage cleanup config not found: ' + str(path)) from error
    except (OSError, ValueError) as error:
        raise ConfigError('Storage cleanup config ' + str(path) + ' is unreadable: ' + str(error)) from error
    if not isinstance(data, dict):
        raise ConfigError('Storage cleanup config ' + str(path) + ' must be a JSON object')
    values = data.get(section)
    if not isinstance(values, dict):
        raise ConfigError('Storage cleanup config ' + str(path) + ' has no "' + section + '" section')
    merged = dict(defaults or {})
    if 'receiptDirectory' in data:
        merged['receiptDirectory'] = data['receiptDirectory']
    merged.update({k: v for k, v in values.items() if not k.startswith('_')})
    missing = [key for key in required if merged.get(key) is None]
    if missing:
        raise ConfigError('Storage cleanup config ' + str(path) + ' section "' + section
                          + '" is missing required key(s): ' + ', '.join(missing))
    return merged
