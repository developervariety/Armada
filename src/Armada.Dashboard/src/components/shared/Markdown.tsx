import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface MarkdownProps {
  children: string;
  className?: string;
}

/**
 * Render markdown text (GitHub-flavored) as sanitized React elements. Used for chat/assistant
 * content so captain replies show headings, lists, code blocks, tables, and links instead of raw
 * markdown source. Links open in a new tab.
 */
export default function Markdown({ children, className }: MarkdownProps) {
  return (
    <div className={`markdown${className ? ` ${className}` : ''}`}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ node: _node, ...props }) => <a {...props} target="_blank" rel="noopener noreferrer" />,
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}
