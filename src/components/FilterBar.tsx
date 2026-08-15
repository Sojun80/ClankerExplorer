import React, { useRef, useEffect } from 'react';
import { Search, X, Code, Asterisk } from 'lucide-react';

interface FilterBarProps {
  filterText: string;
  isFilterRegex: boolean;
  isFilterWildcard: boolean;
  totalCount: number;
  filteredCount: number;
  onFilterChange: (text: string) => void;
  onToggleRegex: () => void;
  onToggleWildcard: () => void;
  onClose: () => void;
}

export const FilterBar: React.FC<FilterBarProps> = ({
  filterText,
  isFilterRegex,
  isFilterWildcard,
  totalCount,
  filteredCount,
  onFilterChange,
  onToggleRegex,
  onToggleWildcard,
  onClose,
}) => {
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
    inputRef.current?.select();
  }, []);

  return (
    <div className="flex items-center gap-2 px-3 py-1 bg-[#0b101c] border-t border-[#1e293b] text-xs select-none">
      <div className="flex items-center gap-1.5 text-[#38bdf8]">
        <Search size={13} />
        <span className="font-mono-code text-[11px] font-bold uppercase">Quick Filter:</span>
      </div>

      <div className="relative flex-1 max-w-md">
        <input
          ref={inputRef}
          type="text"
          value={filterText}
          onChange={(e) => onFilterChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              if (filterText) {
                onFilterChange('');
              } else {
                onClose();
              }
            }
          }}
          placeholder="Filter files (e.g. *.ts, test, error)..."
          className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2 py-0.5 text-xs text-[#f8fafc] font-mono-code outline-none"
        />

        {filterText && (
          <button
            onClick={() => onFilterChange('')}
            className="absolute right-1.5 top-1/2 -translate-y-1/2 text-[#64748b] hover:text-white p-0.5 rounded"
          >
            <X size={12} />
          </button>
        )}
      </div>

      {/* Regex Toggle */}
      <button
        onClick={onToggleRegex}
        title="Toggle Regular Expression mode"
        className={`flex items-center gap-1 px-1.5 py-0.5 rounded border text-[11px] font-mono-code transition-colors ${
          isFilterRegex
            ? 'bg-[#0284c7]/20 border-[#38bdf8] text-[#38bdf8] font-bold'
            : 'border-[#1e293b] text-[#64748b] hover:text-[#94a3b8]'
        }`}
      >
        <Code size={11} />
        <span>.*</span>
      </button>

      {/* Wildcard Toggle */}
      <button
        onClick={onToggleWildcard}
        title="Toggle Wildcard match (* and ?)"
        className={`flex items-center gap-1 px-1.5 py-0.5 rounded border text-[11px] font-mono-code transition-colors ${
          isFilterWildcard
            ? 'bg-[#0284c7]/20 border-[#38bdf8] text-[#38bdf8] font-bold'
            : 'border-[#1e293b] text-[#64748b] hover:text-[#94a3b8]'
        }`}
      >
        <Asterisk size={11} />
        <span>*</span>
      </button>

      {/* Counter */}
      <span className="font-mono-code text-[11px] text-[#94a3b8]">
        Showing <strong className="text-[#38bdf8]">{filteredCount}</strong> of {totalCount} items
      </span>

      {/* Close button */}
      <button
        onClick={onClose}
        title="Close filter (Esc)"
        className="p-1 hover:bg-[#1c2b45] text-[#64748b] hover:text-white rounded ml-auto transition-colors"
      >
        <X size={13} />
      </button>
    </div>
  );
};
