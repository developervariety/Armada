/** Router state that opens Ask Armada on a new conversation with text already in the message box. */
export interface AskNavigationState {
  /** Text placed in the message box (not sent). */
  draft?: string;
}
