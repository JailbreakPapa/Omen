#include "buildoutput.h"
#include <QScrollBar>
#include <QTextCharFormat>

BuildOutput::BuildOutput(QWidget *parent)
    : QPlainTextEdit(parent)
{
    setReadOnly(true);
    setLineWrapMode(QPlainTextEdit::NoWrap);
    setMaximumBlockCount(50000);
    
    // Monospace font
    QFont font("Cascadia Code", 10);
    font.setStyleHint(QFont::Monospace);
    setFont(font);
}

void BuildOutput::appendOutput(const QString &text)
{
    // Parse each line for colors
    QStringList lines = text.split('\n', Qt::SkipEmptyParts);
    for (const QString &line : lines) {
        QString lower = line.toLower();
        
        if (lower.contains("error") || lower.contains("failed") || lower.contains("fatal")) {
            appendError(line);
        } else if (lower.contains("warning")) {
            appendWarning(line);
        } else if (lower.contains("success") || lower.contains("succeeded") || lower.contains("completed")) {
            appendSuccess(line);
        } else {
            appendColored(line, QColor("#C9D1D9"));
        }
    }
}

void BuildOutput::appendError(const QString &text)
{
    appendColored(text, QColor("#F85149"));
}

void BuildOutput::appendWarning(const QString &text)
{
    appendColored(text, QColor("#D29922"));
}

void BuildOutput::appendSuccess(const QString &text)
{
    appendColored(text, QColor("#3FB950"));
}

void BuildOutput::appendInfo(const QString &text)
{
    appendColored(text, QColor("#58A6FF"));
}

void BuildOutput::clearOutput()
{
    clear();
}

void BuildOutput::appendColored(const QString &text, const QColor &color)
{
    QTextCharFormat format;
    format.setForeground(color);
    
    QTextCursor cursor = textCursor();
    cursor.movePosition(QTextCursor::End);
    cursor.insertText(text + "\n", format);
    
    // Auto-scroll
    verticalScrollBar()->setValue(verticalScrollBar()->maximum());
}
