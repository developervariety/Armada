import { AccessibilityInfo, Platform } from 'react-native';

/** Assert the real platform's speech API, including iOS queue semantics. */
export function expectAnnouncement(text: string): void {
  if (Platform.OS === 'ios') expect(AccessibilityInfo.announceForAccessibilityWithOptions).toHaveBeenCalledWith(text, { queue: true });
  else expect(AccessibilityInfo.announceForAccessibility).toHaveBeenCalledWith(text);
}
export function announcementMock(): jest.Mock {
  return (Platform.OS === 'ios' ? AccessibilityInfo.announceForAccessibilityWithOptions : AccessibilityInfo.announceForAccessibility) as jest.Mock;
}
