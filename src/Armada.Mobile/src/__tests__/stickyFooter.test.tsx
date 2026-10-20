import { FormSheet } from '../components/resource/FormSheet';
import { act, fireEvent, screen, within } from '@testing-library/react-native';
import { DeviceEventEmitter, Platform, StyleSheet, Text } from 'react-native';
import { BottomSheet, Button, FormActions, Screen } from '../components/ui';
import { renderWithProviders } from '../test/render';

/** True when the element sits inside a (host) ScrollView, i.e. it moves with the scrolled content. */
function insideScrollView(element: { parent: unknown; type: unknown }): boolean {
  let node = element.parent as { parent: unknown; type: unknown } | null;
  while (node) {
    if (node.type === 'RCTScrollView') return true;
    node = node.parent as { parent: unknown; type: unknown } | null;
  }
  return false;
}

const longForm = Array.from({ length: 40 }, (_v, i) => <Text key={i}>{`Field ${i + 1}`}</Text>);

describe('form footers (primary action reachable without scrolling to the end)', () => {
  it('Screen renders its footer below the scrolling content, not inside it', async () => {
    await renderWithProviders(
      <Screen testID="form" footer={<FormActions><Button label="Save" onPress={jest.fn()} testID="form-save" /></FormActions>}>
        {longForm}
      </Screen>,
    );
    const save = within(screen.getByTestId('form-footer')).getByTestId('form-save');
    expect(insideScrollView(save)).toBe(false);
    expect(insideScrollView(screen.getByText('Field 40'))).toBe(true);
  });

  it('keeps a long form validation error beside its fixed Save action', async () => {
    await renderWithProviders(<FormSheet open title="Long form" initial={{ name: '' }}
      fields={() => [{ key: 'name', kind: 'text', label: 'Name', required: true }, ...Array.from({ length: 40 }, (_, i) => ({ key: `extra${i}`, kind: 'text' as const, label: `Extra ${i}` }))]}
      onClose={() => undefined} onSubmit={async () => undefined} submitLabel="Save" testID="long-form" />);
    await fireEvent.press(screen.getByTestId('long-form-submit'));
    const error = screen.getByTestId('long-form-error');
    expect(error).toHaveTextContent('Name is required.');
    expect(insideScrollView(error)).toBe(false);
    expect(insideScrollView(screen.getByTestId('long-form-submit'))).toBe(false);
  });

  it('Screen lifts its footer above the keyboard by the overlap measured in the window, under a header', async () => {
    jest.replaceProperty(Platform, 'OS', 'ios');
    const nativeMethods = jest.requireActual('@react-native/jest-preset/jest/MockNativeMethods').default as { measureInWindow: jest.Mock };
    // Create Voyage on an iPhone 17 (874 dp): the screen starts below a 100 dp header and ends above an 83 dp tab bar.
    nativeMethods.measureInWindow.mockImplementation((callback: (x: number, y: number, w: number, h: number) => void) => callback(0, 100, 402, 691));
    try {
      await renderWithProviders(
        <Screen testID="form" footer={<FormActions><Button label="Save" onPress={jest.fn()} testID="form-save" /></FormActions>}>
          {longForm}
        </Screen>,
      );
      const lift = () => StyleSheet.flatten(screen.getByTestId('form-keyboard-avoider').props.style).paddingBottom;
      expect(lift()).toBe(0);
      // The keyboard's top at 546 covers the screen's last 245 dp (100 + 691 - 546), header included in the math.
      await act(async () => {
        DeviceEventEmitter.emit(Platform.OS === 'ios' ? 'keyboardWillShow' : 'keyboardDidShow', { endCoordinates: { screenX: 0, screenY: 546, width: 402, height: 328 }, duration: 0, easing: 'keyboard' });
      });
      expect(lift()).toBe(245);
      await act(async () => {
        DeviceEventEmitter.emit(Platform.OS === 'ios' ? 'keyboardWillHide' : 'keyboardDidHide', { endCoordinates: { screenX: 0, screenY: 874, width: 402, height: 0 }, duration: 0, easing: 'keyboard' });
      });
      expect(lift()).toBe(0);
    } finally {
      nativeMethods.measureInWindow.mockReset();
      jest.restoreAllMocks();
    }
  });

  it('BottomSheet renders its footer below the scrolling body, not inside it', async () => {
    await renderWithProviders(
      <BottomSheet open title="Edit" onClose={jest.fn()} closeLabel="Close" testID="sheet"
        footer={<FormActions><Button label="Save" onPress={jest.fn()} testID="sheet-save" /></FormActions>}>
        {longForm}
      </BottomSheet>,
    );
    const save = within(screen.getByTestId('sheet-footer')).getByTestId('sheet-save');
    expect(insideScrollView(save)).toBe(false);
    expect(insideScrollView(screen.getByText('Field 40'))).toBe(true);
  });

  it('a Screen without a footer renders none', async () => {
    await renderWithProviders(<Screen testID="plain">{longForm}</Screen>);
    expect(screen.queryByTestId('plain-footer')).toBeNull();
  });
});
