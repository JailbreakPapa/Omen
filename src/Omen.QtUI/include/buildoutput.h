#ifndef BUILDOUTPUT_H
#define BUILDOUTPUT_H

#include <QPlainTextEdit>

class BuildOutput : public QPlainTextEdit
{
    Q_OBJECT

public:
    explicit BuildOutput(QWidget *parent = nullptr);

    void appendOutput(const QString &text);
    void appendError(const QString &text);
    void appendWarning(const QString &text);
    void appendSuccess(const QString &text);
    void appendInfo(const QString &text);
    void clearOutput();

private:
    void appendColored(const QString &text, const QColor &color);
};

#endif // BUILDOUTPUT_H
