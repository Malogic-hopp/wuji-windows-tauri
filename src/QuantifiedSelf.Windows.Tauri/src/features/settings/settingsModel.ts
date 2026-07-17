import type {
  SettingsFieldError,
  SettingsSnapshot,
  SettingsTheme,
} from '../../bridge/contracts';

export type SettingsFieldName =
  | 'appSettings.theme'
  | 'appSettings.refreshIntervalSeconds'
  | 'appSettings.autoStartAgentWhenAppStarts'
  | 'agentOptions.samplingIntervalSeconds'
  | 'agentOptions.idleThresholdSeconds'
  | 'agentOptions.heartbeatIntervalSeconds'
  | 'agentOptions.staleThresholdSeconds'
  | 'agentOptions.retentionDays'
  | 'agentOptions.enableJsonlJournal'
  | 'agentOptions.enableAgentEventJournal'
  | 'agentOptions.enableSessionMerge'
  | 'agentOptions.maskWindowTitles';

export interface SettingsDraft {
  readonly appSettings: {
    readonly theme: SettingsTheme;
    readonly refreshIntervalSeconds: string;
    readonly autoStartAgentWhenAppStarts: boolean;
  };
  readonly agentOptions: {
    readonly samplingIntervalSeconds: string;
    readonly idleThresholdSeconds: string;
    readonly heartbeatIntervalSeconds: string;
    readonly staleThresholdSeconds: string;
    readonly retentionDays: string;
    readonly enableJsonlJournal: boolean;
    readonly enableAgentEventJournal: boolean;
    readonly enableSessionMerge: boolean;
    readonly maskWindowTitles: boolean;
  };
}

export type SettingsFieldErrors = Partial<Record<SettingsFieldName, string>>;

export type SettingsDraftValue = string | boolean;

const numericFields: ReadonlyArray<SettingsFieldName> = [
  'appSettings.refreshIntervalSeconds',
  'agentOptions.samplingIntervalSeconds',
  'agentOptions.idleThresholdSeconds',
  'agentOptions.heartbeatIntervalSeconds',
  'agentOptions.staleThresholdSeconds',
  'agentOptions.retentionDays',
];

const allowedFields = new Set<SettingsFieldName>([
  'appSettings.theme',
  'appSettings.refreshIntervalSeconds',
  'appSettings.autoStartAgentWhenAppStarts',
  'agentOptions.samplingIntervalSeconds',
  'agentOptions.idleThresholdSeconds',
  'agentOptions.heartbeatIntervalSeconds',
  'agentOptions.staleThresholdSeconds',
  'agentOptions.retentionDays',
  'agentOptions.enableJsonlJournal',
  'agentOptions.enableAgentEventJournal',
  'agentOptions.enableSessionMerge',
  'agentOptions.maskWindowTitles',
]);

export function createSettingsDraft(settings: SettingsSnapshot): SettingsDraft {
  return {
    appSettings: {
      theme: settings.appSettings.theme,
      refreshIntervalSeconds: String(settings.appSettings.refreshIntervalSeconds),
      autoStartAgentWhenAppStarts: settings.appSettings.autoStartAgentWhenAppStarts,
    },
    agentOptions: {
      samplingIntervalSeconds: String(settings.agentOptions.samplingIntervalSeconds),
      idleThresholdSeconds: String(settings.agentOptions.idleThresholdSeconds),
      heartbeatIntervalSeconds: String(settings.agentOptions.heartbeatIntervalSeconds),
      staleThresholdSeconds: String(settings.agentOptions.staleThresholdSeconds),
      retentionDays: String(settings.agentOptions.retentionDays),
      enableJsonlJournal: settings.agentOptions.enableJsonlJournal,
      enableAgentEventJournal: settings.agentOptions.enableAgentEventJournal,
      enableSessionMerge: settings.agentOptions.enableSessionMerge,
      maskWindowTitles: settings.agentOptions.maskWindowTitles,
    },
  };
}

export function updateSettingsDraft(
  draft: SettingsDraft,
  field: SettingsFieldName,
  value: SettingsDraftValue,
): SettingsDraft {
  const next = {
    appSettings: { ...draft.appSettings },
    agentOptions: { ...draft.agentOptions },
  };
  const [group, property] = field.split('.');
  const target = group === 'appSettings' ? next.appSettings : next.agentOptions;
  Object.assign(target, { [property]: value });
  return next;
}

export function parseSettingsDraft(draft: SettingsDraft): {
  readonly settings?: SettingsSnapshot;
  readonly fieldErrors: SettingsFieldErrors;
} {
  const parsed = new Map<SettingsFieldName, number>();
  const fieldErrors: SettingsFieldErrors = {};

  for (const field of numericFields) {
    const rawValue = getNumericDraftValue(draft, field).trim();
    if (rawValue.length === 0) {
      fieldErrors[field] = '请输入整数。';
      continue;
    }
    if (!/^-?\d+$/.test(rawValue)) {
      fieldErrors[field] = '请输入不带小数的整数。';
      continue;
    }
    const value = Number(rawValue);
    if (!Number.isSafeInteger(value)) {
      fieldErrors[field] = '数值过大，请输入较小的整数。';
      continue;
    }
    parsed.set(field, value);
  }

  if (Object.keys(fieldErrors).length > 0) {
    return { fieldErrors };
  }

  return {
    fieldErrors,
    settings: {
      appSettings: {
        theme: draft.appSettings.theme,
        refreshIntervalSeconds: requireParsedValue(parsed, 'appSettings.refreshIntervalSeconds'),
        autoStartAgentWhenAppStarts: draft.appSettings.autoStartAgentWhenAppStarts,
      },
      agentOptions: {
        samplingIntervalSeconds: requireParsedValue(parsed, 'agentOptions.samplingIntervalSeconds'),
        idleThresholdSeconds: requireParsedValue(parsed, 'agentOptions.idleThresholdSeconds'),
        heartbeatIntervalSeconds: requireParsedValue(parsed, 'agentOptions.heartbeatIntervalSeconds'),
        staleThresholdSeconds: requireParsedValue(parsed, 'agentOptions.staleThresholdSeconds'),
        retentionDays: requireParsedValue(parsed, 'agentOptions.retentionDays'),
        enableJsonlJournal: draft.agentOptions.enableJsonlJournal,
        enableAgentEventJournal: draft.agentOptions.enableAgentEventJournal,
        enableSessionMerge: draft.agentOptions.enableSessionMerge,
        maskWindowTitles: draft.agentOptions.maskWindowTitles,
      },
    },
  };
}

export function isSettingsDirty(draft: SettingsDraft, baseline: SettingsDraft): boolean {
  return JSON.stringify(draft) !== JSON.stringify(baseline);
}

export function mapBridgeFieldErrors(
  errors: ReadonlyArray<SettingsFieldError> | undefined,
): SettingsFieldErrors {
  const result: SettingsFieldErrors = {};
  for (const error of errors ?? []) {
    if (allowedFields.has(error.field as SettingsFieldName)) {
      result[error.field as SettingsFieldName] = '请检查此设置值。';
    }
  }
  return result;
}

export function settingsFieldId(field: SettingsFieldName): string {
  return `settings-${field.replaceAll('.', '-')}`;
}

export function formatSettingsInteger(value: string | number, locale?: string): string {
  const numeric = typeof value === 'number' ? value : Number(value);
  if (!Number.isSafeInteger(numeric)) return String(value);
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(numeric);
}

export function readSettingsDraftString(draft: SettingsDraft, field: SettingsFieldName): string {
  return getNumericDraftValue(draft, field);
}

function getNumericDraftValue(draft: SettingsDraft, field: SettingsFieldName): string {
  const [group, property] = field.split('.');
  const source: Record<string, unknown> = group === 'appSettings'
    ? draft.appSettings
    : draft.agentOptions;
  const value = source[property];
  return typeof value === 'string' ? value : '';
}

function requireParsedValue(values: ReadonlyMap<SettingsFieldName, number>, field: SettingsFieldName): number {
  const value = values.get(field);
  if (value === undefined) throw new Error('Validated settings value is missing.');
  return value;
}
